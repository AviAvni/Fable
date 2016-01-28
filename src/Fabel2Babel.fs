module Fabel.Fabel2Babel

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices
open Fabel
open Fabel.AST

type private Context = {
    file: string
    moduleFullName: string
    imports: System.Collections.Generic.Dictionary<string, string>
    }
    
type private BabelFunctionInfo =
    {
        args: Babel.Pattern list
        body: U2<Babel.BlockStatement, Babel.Expression>
        generator: bool
        async: bool
    }
    static member Create (args, body, generator, async) =
        { args=args; body=body; generator=generator; async=async }

type private IBabelCompiler =
    inherit ICompiler
    abstract GetFabelFile: string -> Fabel.File
    abstract GetImport: Context -> string -> Babel.Expression
    abstract TransformExpr: Context -> Fabel.Expr -> Babel.Expression
    abstract TransformStatement: Context -> Fabel.Expr -> Babel.Statement
    abstract TransformFunction: Context -> Fabel.FunctionInfo -> BabelFunctionInfo

let private (|ExprType|) (fexpr: Fabel.Expr) = fexpr.Type
let private (|TransformExpr|) (com: IBabelCompiler) ctx e = com.TransformExpr ctx e
let private (|TransformStatement|) (com: IBabelCompiler) ctx e = com.TransformStatement ctx e

let private (|TestFixture|_|) (decl: Fabel.Declaration) =
    match decl with
    | Fabel.EntityDeclaration (ent, entDecls, entRange) ->
        match ent.TryGetDecorator "TestFixture" with
        | Some _ -> Some (ent, entDecls, entRange)
        | None -> None
    | _ -> None

let private (|Test|_|) (decl: Fabel.Declaration) =
    match decl with
    | Fabel.MemberDeclaration m ->
        match m.Kind, m.TryGetDecorator "Test" with
        | Fabel.Method name, Some _ -> Some (m, name)
        | _ -> None
    | _ -> None
    
let private consBack tail head = head::tail

let private foldRanges (baseRange: SourceLocation) (decls: Babel.Statement list) =
    decls
    |> Seq.choose (fun x -> x.loc)
    |> Seq.fold (fun _ x -> x) baseRange
    |> (+) baseRange
    
let private ident (id: Fabel.Ident) =
    Babel.Identifier id.name

let private identFromName name =
    let sanitizedName = Naming.sanitizeIdent (fun _ -> false) name
    Babel.Identifier name
    
let private get left propName =
    if Naming.identForbiddenChars.IsMatch propName
    then Babel.MemberExpression(left, Babel.StringLiteral propName, true)
    else Babel.MemberExpression(left, Babel.Identifier propName, false)
    :> Babel.Expression
    
let private getExpr com ctx (TransformExpr com ctx expr) (property: Fabel.Expr) =
    let property, computed =
        match property with
        | Fabel.Value (Fabel.StringConst name)
            when Naming.identForbiddenChars.IsMatch name = false ->
            Babel.Identifier (name) :> Babel.Expression, false
        | TransformExpr com ctx property -> property, true
    match expr with
    | :? Babel.EmptyExpression -> property
    | _ -> Babel.MemberExpression (expr, property, computed) :> Babel.Expression

let private typeRef (com: IBabelCompiler) ctx file fullName: Babel.Expression =
    let getDiff s1 s2 =
        let split (s: string) =
            s.Split('.') |> Array.toList
        let rec removeCommon (xs1: string list) (xs2: string list) =
            match xs1, xs2 with
            | x1::xs1, x2::xs2 when x1 = x2 -> removeCommon xs1 xs2
            | _ -> xs2
        removeCommon (split s1) (split s2)
    let rec makeExpr (members: string list) (baseExpr: Babel.Expression option) =
        match baseExpr with
        | Some baseExpr ->
            match members with
            | [] -> baseExpr
            | m::ms -> get baseExpr m |> Some |> makeExpr ms 
        | None ->
            match members with
            | [] -> upcast Babel.EmptyExpression()
            | m::ms -> identFromName m :> Babel.Expression |> Some |> makeExpr ms
    match file with
    | None -> failwithf "Cannot reference type: %s" fullName
    | Some file ->
        let file = com.GetFabelFile file
        file.ExternalEntities
        |> List.tryFind (fun ext ->
            fullName = ext.FullName || fullName.StartsWith ext.FullName)
        |> function
            | Some (Fabel.ImportModule (ns, modName)) ->
                Some (com.GetImport ctx modName)
                |> makeExpr (getDiff ns fullName)
            | Some (Fabel.GlobalModule ns) ->
                makeExpr (getDiff ns fullName) None
            | None when ctx.file <> file.FileName ->
                Some (com.GetImport ctx file.FileName)
                |> makeExpr (getDiff file.Root.FullName fullName)
            | None ->
                makeExpr (getDiff ctx.moduleFullName fullName) None

let private assign range left right =
    Babel.AssignmentExpression(AssignEqual, left, right, ?loc=range)
    :> Babel.Expression
    
let private block (com: IBabelCompiler) ctx range (exprs: Fabel.Expr list) =
    let exprs = match exprs with
                | [Fabel.Sequential (statements,_)] -> statements
                | _ -> exprs
    Babel.BlockStatement (exprs |> List.map (com.TransformStatement ctx), ?loc=range)
    
let private returnBlock e =
    Babel.BlockStatement([Babel.ReturnStatement(e, ?loc=e.loc)], ?loc=e.loc)

let private func (com: IBabelCompiler) ctx funcInfo =
    let f = com.TransformFunction ctx funcInfo
    let body = match f.body with U2.Case1 block -> block | U2.Case2 expr -> returnBlock expr
    f.args, body, f.generator, f.async

let private funcExpression (com: IBabelCompiler) ctx funcInfo =
    let args, body, generator, async = func com ctx funcInfo
    Babel.FunctionExpression (args, body, generator, async, ?loc=body.loc)

let private funcDeclaration (com: IBabelCompiler) ctx id funcInfo =
    let args, body, generator, async = func com ctx funcInfo
    Babel.FunctionDeclaration(id, args, body, generator, async, ?loc=body.loc)

let private funcArrow (com: IBabelCompiler) ctx funcInfo =
    let f = com.TransformFunction ctx funcInfo
    let range = match f.body with U2.Case1 x -> x.loc | U2.Case2 x -> x.loc
    Babel.ArrowFunctionExpression (f.args, f.body, f.async, ?loc=range)
    :> Babel.Expression

/// Immediately Invoked Function Expression
let private iife (com: IBabelCompiler) ctx (expr: Fabel.Expr) =
    let lambda =
        Fabel.FunctionInfo.Create(Fabel.Immediate, [], false, expr)
        |> funcExpression com ctx
    Babel.CallExpression (lambda, [], ?loc=expr.Range)

let private varDeclaration range var value =
    Babel.VariableDeclaration (
        Babel.VariableDeclarationKind.Var,
        [Babel.VariableDeclarator (var, value, ?loc=range)],
        ?loc = range)

let private transformStatement com ctx (expr: Fabel.Expr): Babel.Statement =
    match expr with
    | Fabel.Loop (loopKind,_) ->
        match loopKind with
        | Fabel.While (TransformExpr com ctx guard, body) ->
            upcast Babel.WhileStatement (guard, block com ctx body.Range [body], ?loc=expr.Range)
        | Fabel.ForOf (var, TransformExpr com ctx enumerable, body) ->
            // enumerable doesn't go in VariableDeclator.init but in ForOfStatement.right 
            let var =
                Babel.VariableDeclaration(
                    Babel.VariableDeclarationKind.Var,
                    [Babel.VariableDeclarator (ident var)])
            upcast Babel.ForOfStatement (
                U2.Case1 var, enumerable, block com ctx body.Range [body], ?loc=expr.Range)
        | Fabel.For (var, TransformExpr com ctx start,
                        TransformExpr com ctx limit, body, isUp) ->
            upcast Babel.ForStatement (
                block com ctx body.Range [body],
                start |> varDeclaration None (ident var) |> U2.Case1,
                Babel.BinaryExpression (BinaryOperator.BinaryLessOrEqual, ident var, limit),
                Babel.UpdateExpression (UpdateOperator.UpdatePlus, false, ident var), ?loc=expr.Range)

    | Fabel.Set (callee, property, TransformExpr com ctx value, range) ->
        let left =
            match property with
            | None -> com.TransformExpr ctx callee
            | Some property -> getExpr com ctx callee property
        upcast Babel.ExpressionStatement (assign range left value, ?loc = expr.Range)

    | Fabel.VarDeclaration (var, TransformExpr com ctx value, _isMutable) ->
        varDeclaration expr.Range (ident var) value :> Babel.Statement

    | Fabel.TryCatch (body, catch, finalizer, _) ->
        let handler =
            catch |> Option.map (fun (param, body) ->
                Babel.CatchClause (ident param,
                    block com ctx body.Range [body], ?loc=body.Range))
        let finalizer =
            match finalizer with
            | None -> None
            | Some e -> Some (block com ctx e.Range [e])
        upcast Babel.TryStatement (block com ctx expr.Range [body],
            ?handler=handler, ?finalizer=finalizer, ?loc=expr.Range)

    | Fabel.IfThenElse (TransformExpr com ctx guardExpr,
                        TransformStatement com ctx thenExpr, elseExpr, _) ->
        let elseExpr =
            match elseExpr with
            | Fabel.Value Fabel.Null -> None
            | _ -> Some (com.TransformStatement ctx elseExpr)
        upcast Babel.IfStatement (
            guardExpr, thenExpr, ?alternate=elseExpr, ?loc=expr.Range)

    | Fabel.Sequential _ ->
        failwithf "Sequence when single statement expected in %A: %A" expr.Range expr 

    // Expressions become ExpressionStatements
    | Fabel.Value _ | Fabel.Get _ | Fabel.Apply _ ->
        upcast Babel.ExpressionStatement (com.TransformExpr ctx expr, ?loc=expr.Range)

let private transformExpr (com: IBabelCompiler) ctx (expr: Fabel.Expr): Babel.Expression =
    match expr with
    | Fabel.Value kind ->
        match kind with
        | Fabel.ImportRef (import, prop) ->
            match prop with
            | Some prop -> get (com.GetImport ctx import) prop
            | None -> com.GetImport ctx import
        | Fabel.This _ -> upcast Babel.ThisExpression (?loc=expr.Range)
        | Fabel.Super _ -> upcast Babel.Super (?loc=expr.Range)
        | Fabel.Null -> upcast Babel.NullLiteral (?loc=expr.Range)
        | Fabel.IdentValue {name=name} -> upcast Babel.Identifier (name, ?loc=expr.Range)
        | Fabel.IntConst (x,_) -> upcast Babel.NumericLiteral (U2.Case1 x, ?loc=expr.Range)
        | Fabel.FloatConst (x,_) -> upcast Babel.NumericLiteral (U2.Case2 x, ?loc=expr.Range)
        | Fabel.StringConst x -> upcast Babel.StringLiteral (x, ?loc=expr.Range)
        | Fabel.BoolConst x -> upcast Babel.BooleanLiteral (x, ?loc=expr.Range)
        | Fabel.RegexConst (source, flags) -> upcast Babel.RegExpLiteral (source, flags, ?loc=expr.Range)
        | Fabel.Lambda finfo -> funcArrow com ctx finfo
        | Fabel.ArrayConst (items, kind) -> failwith "TODO: Array initializers"
        | Fabel.ObjExpr _ -> failwith "TODO: Object expressions"
        | Fabel.TypeRef typ ->
            match typ with
            | Fabel.DeclaredType typEnt ->
                let typFullName =
                    if Option.isSome (typEnt.TryGetDecorator "Erase") 
                    then typEnt.FullName.Substring(0, typEnt.FullName.LastIndexOf ".")
                    else typEnt.FullName
                typeRef com ctx typEnt.File typFullName
            | _ -> failwithf "Not supported type reference: %A" typ
        | Fabel.LogicalOp _ | Fabel.BinaryOp _ | Fabel.UnaryOp _ ->
            failwithf "Unexpected stand-alone operation: %A" expr

    | Fabel.Apply (callee, args, isPrimaryConstructor, _, _) ->
        match callee, args with
        | Fabel.Value (Fabel.LogicalOp op), [left; right] ->
            failwith "TODO: Logical operations"
        | Fabel.Value (Fabel.UnaryOp op), [TransformExpr com ctx operand as expr] ->
            upcast Babel.UnaryExpression (op, operand, ?loc=expr.Range)
        | Fabel.Value (Fabel.BinaryOp op), [TransformExpr com ctx left; TransformExpr com ctx right] ->
            upcast Babel.BinaryExpression (op, left, right, ?loc=expr.Range)
        | _ ->
            let callee = com.TransformExpr ctx callee
            let args = args |> List.map (com.TransformExpr ctx >> U2<_,_>.Case1)
            if isPrimaryConstructor
            then upcast Babel.NewExpression (callee, args, ?loc=expr.Range)
            else upcast Babel.CallExpression (callee, args, ?loc=expr.Range)

    | Fabel.Get (callee, property, _) ->
        getExpr com ctx callee property

    | Fabel.IfThenElse (TransformExpr com ctx guardExpr,
                        TransformExpr com ctx thenExpr,
                        TransformExpr com ctx elseExpr, _) ->
        upcast Babel.ConditionalExpression (
            guardExpr, thenExpr, elseExpr, ?loc = expr.Range)

    | Fabel.Sequential (statements, _) ->
        Babel.BlockStatement (statements |> List.map (com.TransformStatement ctx), [])
        |> fun block -> upcast Babel.DoExpression (block)

    | Fabel.TryCatch _ ->
        upcast (iife com ctx expr)

    | Fabel.Loop _ | Fabel.Set _  | Fabel.VarDeclaration _ ->
        failwithf "Statement when expression expected in %A: %A" expr.Range expr 
    
let private transformFunction com ctx (finfo: Fabel.FunctionInfo) =
    let generator, async =
        match finfo.kind with
        | Fabel.Immediate -> false, false
        | Fabel.Generator -> true, false
        | Fabel.Async -> false, true
    let args: Babel.Pattern list =
        if finfo.restParams then failwith "TODO: RestParams"
        else finfo.args |> List.map (fun x -> upcast ident x)
    let body: U2<Babel.BlockStatement, Babel.Expression> =
        match finfo.body with
        | ExprType (Fabel.PrimitiveType Fabel.Unit) ->
            block com ctx finfo.body.Range [finfo.body] |> U2.Case1
        | Fabel.TryCatch (tryBody, handler, finalizer, tryRange) ->
            let handler =
                handler |> Option.map (fun (param, body) ->
                    let clause = transformExpr com ctx body |> returnBlock
                    Babel.CatchClause (ident param, clause, ?loc=body.Range))
            let finalizer =
                finalizer |> Option.map (fun x -> block com ctx x.Range [x])
            let tryBody =
                transformExpr com ctx tryBody |> returnBlock
            Babel.BlockStatement (
                [Babel.TryStatement (tryBody, ?handler=handler, ?finalizer=finalizer, ?loc=tryRange)],
                ?loc = finfo.body.Range) |> U2.Case1
        | _ ->
            transformExpr com ctx finfo.body |> U2.Case2
    BabelFunctionInfo.Create(args, body, generator, async)
    
let private transformTestSuite com ctx decls: Babel.CallExpression =
    failwith "TODO: TestSuite members"
    
let private transformClass com ctx classRange (baseClass: Fabel.EntityLocation option) decls =
    let declareMember range kind name (finfo: Fabel.FunctionInfo) isStatic =
        let name, computed: Babel.Expression * bool =
            if Naming.identForbiddenChars.IsMatch name
            then upcast Babel.StringLiteral name, true
            else upcast Babel.Identifier name, false
        let finfo = transformFunction com ctx finfo
        let body = match finfo.body with U2.Case1 e -> e | U2.Case2 e -> returnBlock e
        // TODO: Optimization: remove null statement that F# compiler adds at the bottom of constructors
        Babel.ClassMethod(range, kind, name, finfo.args, body, computed, isStatic)
    let baseClass = baseClass |> Option.map (fun loc ->
        typeRef com ctx (Some loc.file) loc.fullName)
    decls
    |> List.map (function
        | Fabel.MemberDeclaration m ->
            let kind, name, isStatic =
                match m.Kind with
                | Fabel.Constructor -> Babel.ClassConstructor, "constructor", false
                | Fabel.Method name -> Babel.ClassFunction, name, m.IsStatic
                | Fabel.Getter name -> Babel.ClassGetter, name, m.IsStatic
                | Fabel.Setter name -> Babel.ClassSetter, name, m.IsStatic
            declareMember m.Range kind name m.Function isStatic
        | Fabel.ActionDeclaration _
        | Fabel.EntityDeclaration _ as decl ->
            failwithf "Unexpected declaration in class: %A" decl)
    |> List.map U2<_,Babel.ClassProperty>.Case1
    |> fun meths -> Babel.ClassExpression(classRange, Babel.ClassBody(classRange, meths), ?super=baseClass)

// TODO: Keep track of sanitized member names to be sure they don't clash? 
let private declareModMember range var name isPublic modIdent expr =
    let var = match var with Some x -> x | None -> identFromName name
    match isPublic, modIdent with
    | true, Some modIdent -> assign (Some range) (get modIdent name) expr 
    | _ -> expr
    |> varDeclaration (Some range) var :> Babel.Statement

let private compileModMember com ctx (m: Fabel.Member) =
    let expr, name =
        match m.Kind with
        | Fabel.Getter name ->
            let finfo =
                Fabel.FunctionInfo.Create(Fabel.Immediate, [], false, m.Function.body)
                |> transformFunction com ctx
            match finfo.body with
            | U2.Case2 e -> e, name
            | U2.Case1 e -> Babel.DoExpression(e, ?loc=e.loc) :> Babel.Expression, name
        | Fabel.Method name ->
            upcast funcExpression com ctx m.Function, name
        | Fabel.Constructor | Fabel.Setter _ ->
            failwithf "Unexpected member in module: %A" m.Kind
    let memberRange =
        match expr.loc with Some loc -> m.Range + loc | None -> m.Range
    declareModMember memberRange None name false None expr

// Compile tests using Mocha.js BDD interface
let private compileTest com ctx (test: Fabel.Member) name =
    let testName =
        Babel.StringLiteral name :> Babel.Expression
    let testBody =
        funcExpression com ctx test.Function :> Babel.Expression
    let testRange =
        match testBody.loc with
        | Some loc -> test.Range + loc | None -> test.Range
    // it('Test name', function() { /* Tests */ });
    Babel.ExpressionStatement(
        Babel.CallExpression(Babel.Identifier "it",
            [U2.Case1 testName; U2.Case1 testBody], testRange))
        :> Babel.Statement

let private compileTestFixture com ctx (fixture: Fabel.Entity) testDecls testRange =
    let testDesc =
        Babel.StringLiteral fixture.Name :> Babel.Expression
    let testBody =
        Babel.FunctionExpression([],
            Babel.BlockStatement (testDecls, ?loc=Some testRange), ?loc=Some testRange)
        :> Babel.Expression
    Babel.ExpressionStatement(
        Babel.CallExpression(Babel.Identifier "describe",
            [U2.Case1 testDesc; U2.Case1 testBody],
            testRange)) :> Babel.Statement
                
let rec private compileModule com ctx modIdent (ent: Fabel.Entity) entDecls entRange =
    let nestedIdent, protectedIdent =
        let memberNames =
            entDecls |> Seq.choose (function
                | Fabel.EntityDeclaration (ent,_,_) -> Some ent.Name
                | Fabel.ActionDeclaration ent -> None
                | Fabel.MemberDeclaration m ->
                    match m.Kind with
                    | Fabel.Method name | Fabel.Getter name -> Some name
                    | Fabel.Constructor | Fabel.Setter _ -> None)
            |> Set.ofSeq
        identFromName ent.Name,
        // Protect module identifier against members with same name
        Babel.Identifier (Naming.sanitizeIdent memberNames.Contains ent.Name)
    let nestedDecls =
        let ctx = { ctx with moduleFullName = ent.FullName }
        transformModDecls com ctx (Some protectedIdent) entDecls
    let nestedRange =
        foldRanges entRange nestedDecls
    Babel.CallExpression(
        Babel.FunctionExpression([protectedIdent],
            Babel.BlockStatement (nestedDecls, ?loc=Some nestedRange),
            ?loc=Some nestedRange),
        [U2.Case1 (upcast Babel.ObjectExpression [])],
        nestedRange)
    // var NestedMod = ParentMod.NestedMod = function (/* protected */ NestedMod_1) {
    //     var privateVar = 1;
    //     var publicVar = NestedMod_1.publicVar = 2;
    //     var NestedMod = NestedMod_1.NestedMod = {};
    // }({});
    |> declareModMember nestedRange (Some nestedIdent) ent.Name ent.IsPublic modIdent

and private transformModDecls com ctx modIdent decls =
    decls |> List.fold (fun acc decl ->
        match decl with
        | Test (test, name) ->
            compileTest com ctx test name
            |> consBack acc
        | TestFixture (fixture, testDecls, testRange) ->
            let testDecls =
                let ctx = { ctx with moduleFullName = fixture.FullName } 
                transformModDecls com ctx None testDecls
            let testRange = foldRanges testRange testDecls
            compileTestFixture com ctx fixture testDecls testRange
            |> consBack acc  
        | Fabel.ActionDeclaration e ->
            transformStatement com ctx e
            |> consBack acc
        | Fabel.MemberDeclaration m ->
            compileModMember com ctx m
            |> consBack acc
        | Fabel.EntityDeclaration (ent, entDecls, entRange) ->
            match ent.Kind with
            // Interfaces, attribute or erased declarations shouldn't reach this point
            | Fabel.Interface ->
                failwithf "Cannot emit interface declaration into JS: %s" ent.FullName
            | Fabel.Class _ | Fabel.Union | Fabel.Record ->
                let baseClass = match ent.Kind with Fabel.Class x -> x | _ -> None
                // Don't create a new context for class declarations
                transformClass com ctx entRange baseClass entDecls
                |> declareModMember entRange None ent.Name ent.IsPublic modIdent
                |> consBack acc
            | Fabel.Module ->
                compileModule com ctx modIdent ent entDecls entRange
                |> consBack acc) []
    |> fun decls ->
        match modIdent with
        | Some modIdent -> (Babel.ReturnStatement modIdent :> Babel.Statement)::decls
        | None -> decls
        |> List.rev

let private makeCompiler (com: ICompiler) (files: Fabel.File list) =
    let fileMap =
        files |> Seq.map (fun f -> f.FileName, f) |> Map.ofSeq
    { new IBabelCompiler with
        member bcom.GetFabelFile fileName =
            Map.tryFind fileName fileMap
            |> function Some file -> file
                      | None -> failwithf "File not parsed: %s" fileName
        member bcom.GetImport ctx moduleName =
            match ctx.imports.TryGetValue moduleName with
            | true, import ->
                upcast Babel.Identifier import
            | false, _ ->
                let import = Naming.getImportModuleIdent ctx.imports.Count
                ctx.imports.Add(moduleName, import)
                upcast Babel.Identifier import
        member bcom.TransformExpr ctx e = transformExpr bcom ctx e
        member bcom.TransformStatement ctx e = transformStatement bcom ctx e
        member bcom.TransformFunction ctx e = transformFunction bcom ctx e
      interface ICompiler with
        member __.Options = com.Options }

let transformFiles (com: ICompiler) (files: Fabel.File list): Babel.Program list =
    let babelCom = makeCompiler com files
    files |> List.choose (fun file ->
        match file.Declarations with
        | [] -> None
        | _ ->
            let ctx = {
                file = file.FileName
                moduleFullName = file.Root.FullName
                imports = System.Collections.Generic.Dictionary<_,_>()
            }
            let isRootTest =
                file.Root.TryGetDecorator "TestFixture" |> Option.isSome
            let rootIdent =
                if isRootTest
                then None
                else Naming.getImportModuleIdent -1 |> Babel.Identifier |> Some
            let rootDecls = transformModDecls babelCom ctx rootIdent file.Declarations
            let rootRange = foldRanges SourceLocation.Empty rootDecls
            let rootMod =
                if isRootTest then
                    compileTestFixture com ctx file.Root rootDecls rootRange
                    |> U2.Case1
                else
                    Babel.ExportDefaultDeclaration(
                        U2.Case2 (Babel.CallExpression(
                                    Babel.FunctionExpression(
                                        [rootIdent.Value],
                                        Babel.BlockStatement(rootDecls, ?loc=Some rootRange),
                                        ?loc=Some rootRange),
                                    [U2.Case1 (upcast Babel.ObjectExpression [])],
                                    rootRange) :> Babel.Expression),
                            rootRange) :> Babel.ModuleDeclaration |> U2.Case2
            // Add imports
            // TODO: Import namespaces `import * as $M1 from foo`
            let rootDecls =
                ctx.imports |> Seq.fold (fun acc import ->
                    let specifier =
                        Babel.Identifier import.Value
                        |> Babel.ImportDefaultSpecifier
                        |> U3.Case2
                    Babel.ImportDeclaration(
                        [specifier],
                        Babel.StringLiteral import.Key)
                    :> Babel.ModuleDeclaration
                    |> U2.Case2
                    |> consBack acc) [rootMod]
            Babel.Program (rootRange, rootDecls) |> Some)         