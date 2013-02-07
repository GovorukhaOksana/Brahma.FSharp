﻿// Copyright (c) 2012, 2013 Semyon Grigorev <rsdpisuy@gmail.com>
// All rights reserved.
// 
// The contents of this file are made available under the terms of the
// Eclipse Public License v1.0 (the "License") which accompanies this
// distribution, and is available at the following URL:
// http://www.opensource.org/licenses/eclipse-1.0.php
// 
// Software distributed under the License is distributed on an "AS IS" basis,
// WITHOUT WARRANTY OF ANY KIND, either expressed or implied. See the License for
// the specific language governing rights and limitations under the License.
// 
// By using this software in any fashion, you are agreeing to be bound by the
// terms of the License.

module Brahma.FSharp.OpenCL.Translator.Body

open Microsoft.FSharp.Quotations
open Brahma.FSharp.OpenCL.AST

let clearContext (targetContext:TargetContext<'a,'b>) =
    let c = new TargetContext<'a,'b>()
    c.Namer <- targetContext.Namer
    c.Flags <- targetContext.Flags
    c

let rec private translateBinding (var:Var) newName (expr:Expr) (targetContext:TargetContext<_,_>) =    
    let body,tContext = (*TranslateAsExpr*) translateCond expr targetContext
    let vType = Type.Translate(var.Type)    
    new VarDecl<Lang>(vType,newName,Some body)

and private translateCall exprOpt (mInfo:System.Reflection.MethodInfo) _args targetContext =
    let args,tContext = 
        let a,c = _args |> List.fold (fun (res,tc) a -> let r,tc = translateCond a tc in r::res,tc) ([],targetContext)
        a |> List.rev , c
    match mInfo.Name.ToLowerInvariant() with
    | "op_multiply"            -> new Binop<_>(Mult,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_addition"            -> new Binop<_>(Plus,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_division"            -> new Binop<_>(Div,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_lessthan"            -> new Binop<_>(Less,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_lessthanorequal"     -> new Binop<_>(LessEQ,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_greaterthan"         -> new Binop<_>(Great,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_greaterthanorequal"  -> new Binop<_>(GreatEQ,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_equality"            -> new Binop<_>(EQ,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_inequality"          -> new Binop<_>(NEQ,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_subtraction"         -> new Binop<_>(Minus,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_unarynegation"       -> new Unop<_>(UOp.Minus,args.[0]) :> Statement<_>,tContext
    | "op_modulus"             -> new Binop<_>(Remainder,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_bitwiseand"          -> new Binop<_>(BitAnd,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_bitwiseor"          -> new Binop<_>(BitOr,args.[0],args.[1]) :> Statement<_>,tContext
    | "op_lessbangplusgreater"
    | "op_lessbangplus"        -> 
        tContext.Flags.enableAtomic <- true
        new FunCall<_>("atom_add",[new Pointer<_>(args.[0]);args.[1]]) :> Statement<_>,tContext
    | "op_lessbangmunus"
    | "op_lessbangmunusgreater"->
        tContext.Flags.enableAtomic <- true
        new FunCall<_>("atom_sub",[new Pointer<_>(args.[0]);args.[1]]) :> Statement<_>,tContext 
    | "op_lesssbanggreater"
    | "op_lesssbang"           -> 
        tContext.Flags.enableAtomic <- true
        new FunCall<_>("atom_xchg",[new Pointer<_>(args.[0]);args.[1]]) :> Statement<_>,tContext 
    | "todouble"               -> new Cast<_>( args.[0],new PrimitiveType<_>(PTypes<_>.Float)):> Statement<_>,tContext
    | "toint"                  -> new Cast<_>( args.[0],new PrimitiveType<_>(PTypes<_>.Int)):> Statement<_>,tContext
    | "tosingle"               -> new Cast<_>( args.[0],new PrimitiveType<_>(PTypes<_>.Float)):> Statement<_>,tContext
    | "acos" | "asin" | "atan"
    | "cos" | "cosh" | "exp"
    | "floor" | "log" | "log10"
    | "pow" | "sin" | "sinh" | "sqrt"
    | "tan" | "tanh" as fName ->
        if mInfo.DeclaringType.AssemblyQualifiedName.StartsWith("System.Math")
            || mInfo.DeclaringType.AssemblyQualifiedName.StartsWith("Microsoft.FSharp.Core.Operators")
        then FunCall<_>(fName,args) :> Statement<_>,tContext
        else failwithf "Seems, thet you use math function with name %s not from System.Math. or Microsoft.FSharp.Core.Operators" fName
    | "abs" as fName ->
        if mInfo.DeclaringType.AssemblyQualifiedName.StartsWith("Microsoft.FSharp.Core.Operators")
        then FunCall<_>("fabs",args) :> Statement<_>,tContext
        else failwithf "Seems, thet you use math function with name %s not from System.Math. or Microsoft.FSharp.Core.Operators" fName
    | "powinteger" as fName ->
        if mInfo.DeclaringType.AssemblyQualifiedName.StartsWith("Microsoft.FSharp.Core.Operators")
        then FunCall<_>("powr",args) :> Statement<_>,tContext
        else failwithf "Seems, thet you use math function with name %s not from System.Math. or Microsoft.FSharp.Core.Operators" fName

    | "setarray" -> 
        let item = new Item<_>(args.[0],args.[1])
        new Assignment<_>(new Property<_>(PropertyType.Item(item)),args.[2]) :> Statement<_>
        , tContext
    | "getarray" -> new Item<_>(args.[0],args.[1]) :> Statement<_>, tContext
    | "_byte"    -> args.[0] :> Statement<_>, tContext
    //| "local"    -> 
    //| "zerocreate" ->
    | c -> failwithf "Unsupporte call: %s" c

and private itemHelper exprs hostVar tContext =
    let idx,tContext = 
        match exprs with
        | hd::_ -> TranslateAsExpr hd tContext
        | [] -> failwith "Array index missed!"
    let hVar = 
        match hostVar with
        | Some(v,_) -> v
        | None -> failwith "Host var missed!"
    idx,tContext,hVar

and private transletaPropGet exprOpt (propInfo:System.Reflection.PropertyInfo) exprs targetContext =
    let hostVar = exprOpt |> Option.map(fun e -> TranslateAsExpr e targetContext)
    match propInfo.Name.ToLowerInvariant() with
    | "globalid0i" | "globalid0" -> new FunCall<_>("get_global_id",[Const(PrimitiveType<_>(Int),"0")]) :> Expression<_>, targetContext
    | "globalid1i" | "globalid1" ->  new FunCall<_>("get_global_id",[Const(PrimitiveType<_>(Int),"1")]) :> Expression<_>, targetContext
    | "item" -> 
        let idx,tContext,hVar = itemHelper exprs hostVar targetContext
        new Item<_>(hVar,idx) :> Expression<_>, tContext
    | x -> failwithf "Unsupported property in kernel: %A"  x

and private transletaPropSet exprOpt (propInfo:System.Reflection.PropertyInfo) exprs newVal targetContext =    
    let hostVar = exprOpt |> Option.map(fun e -> TranslateAsExpr e targetContext)
    let newVal,tContext = TranslateAsExpr newVal (match hostVar with Some(v,c) -> c | None -> targetContext)
    match propInfo.Name.ToLowerInvariant() with
    | "item" -> 
        let idx,tContext,hVar = itemHelper exprs hostVar tContext
        let item = new Item<_>(hVar,idx)        
        new Assignment<_>(new Property<_>(PropertyType.Item(item)),newVal) :> Statement<_>
        , tContext
    | x -> failwithf "Unsupported property in kernel: %A"  x  

and TranslateAsExpr expr (targetContext:TargetContext<_,_>) =
    let (r:Node<_>),tc = Translate expr (targetContext:TargetContext<_,_>)
    (r  :?> Expression<_>) ,tc

and getVar (clVarName:string) (targetContext:TargetContext<_,_>) =
    new Variable<_>(clVarName)

and translateVar (var:Var) (targetContext:TargetContext<_,_>) =
    let vName = targetContext.Namer.GetCLVarName var.Name
    match vName with
    | Some n -> getVar n targetContext
    | None -> failwith "Seems, that you try to use variable, that declared out of quotation. Please, pass it as quoted function's parametaer."

and translateValue (value:obj) (sType:System.Type) =
    let _type = Type.Translate sType
    let v =
        let s = string value 
        if sType.Name.ToLowerInvariant() = "boolean"
        then            
            if s.ToLowerInvariant() = "false" then "0" else "1"
        else s 
    new Const<_>(_type, v)

and translateVarSet (var:Var) (expr:Expr) targetContext =
    let var = translateVar var targetContext
    let expr,tContext = translateCond(*TranslateAsExpr*) expr targetContext
    new Assignment<_>(new Property<_>(PropertyType.Var var),expr),tContext

and translateCond (cond:Expr) targetContext =
    match cond with
    | Patterns.IfThenElse(cond,_then,_else) ->
        let l,tContext = translateCond cond targetContext
        let r,tContext = translateCond _then tContext
        let e,tContext = translateCond _else tContext
        new Binop<_>(Or, new Binop<_>(And,l,r),e) :> Expression<_> , tContext
    | Patterns.Value (v,t) ->
        let str =  (string v).ToLowerInvariant()
        let r = new Const<_>(new PrimitiveType<_>(PTypes.Int), (if str = "true" then "255" elif str = "false" then "0" else str))
        r :> Expression<_>, targetContext 
    | _ -> TranslateAsExpr cond targetContext

and toStb (s:Node<_>) =
    match s with
    | :? StatementBlock<_> as s -> s
    | x -> new StatementBlock<_>(new ResizeArray<_>([x :?> Statement<_>]))

and translateIf (cond:Expr) (thenBranch:Expr) (elseBranch:Expr) targetContext =
    let cond,tContext = translateCond cond targetContext
    let _then,tContext = 
        let t,tc = Translate thenBranch (clearContext targetContext)
        toStb t, tc
    let _else,tContext = 
        match elseBranch with
        | Patterns.Value(null,sType) -> None,tContext
        | _ -> 
            let r,tContext = Translate elseBranch (clearContext targetContext)
            Some (toStb r), tContext
    new IfThenElse<_>(cond,_then, _else), targetContext

and translateForIntegerRangeLoop (i:Var) (from:Expr) (_to:Expr) (_do:Expr) (targetContext:TargetContext<_,_>) =
    let iName = targetContext.Namer.LetStart i.Name
    let v = getVar iName targetContext
    let var = translateBinding i iName from targetContext
    let condExpr,tContext = TranslateAsExpr _to targetContext
    targetContext.Namer.LetIn i.Name
    let body,tContext = Translate _do (clearContext targetContext)
    let cond = new Binop<_>(LessEQ, v, condExpr)
    let condModifier = new Unop<_>(UOp.Incr,v)   
    targetContext.Namer.LetOut()
    new ForIntegerLoop<_>(var,cond, condModifier,toStb body),targetContext

and translateWhileLoop condExpr bodyExpr targetContext =
    let nCond,tContext = translateCond condExpr targetContext
    let nBody,tContext = Translate bodyExpr tContext
    new WhileLoop<_>(nCond, toStb nBody), tContext 

and translateSeq expr1 expr2 (targetContext:TargetContext<_,_>) =
    let decls = new ResizeArray<_>(targetContext.VarDecls)
    targetContext.VarDecls.Clear()
    let nExpr1,tContext = Translate expr1 targetContext
    let nExpr2,tContext = Translate expr2 tContext
    let stmt = 
        match (nExpr1:Node<_>),(nExpr2:Node<_>) with
        | (:? StatementBlock<Lang> as s1),(:? StatementBlock<Lang> as s2) ->
            decls.AddRange(s1.Statements)
            decls.AddRange(s2.Statements)
            new StatementBlock<Lang>(decls)
        | (:? StatementBlock<Lang> as s1),s2 ->
            decls.AddRange(s1.Statements)
            decls.Add (s2 :?> Statement<_>)
            new StatementBlock<Lang>(decls)
        | s1,(:? StatementBlock<Lang> as s2) ->
            decls.Add(s1:?> Statement<_>)
            decls.AddRange s2.Statements
            new StatementBlock<Lang>(decls)
        | s1,s2 ->
            decls.Add(s1:?> Statement<_>)
            decls.Add (s2 :?> Statement<_>)            
            new StatementBlock<Lang>(decls)
    stmt, tContext

and translateApplication expr1 expr2 targetContext =
    let rec go expr _vals args = 
        match expr with
        | Patterns.Lambda (v,e) ->
            go e _vals (v::args)
        | Patterns.Application (e1,e2) ->
            go e1 (e2::_vals) args
        | e ->
            let d =
                if _vals.Length = args.Length
                then List.zip (List.rev args) _vals |> dict
                else failwith "Partial evaluation is not supported in kernel function."
            e.Substitute (fun v -> if d.ContainsKey v then Some d.[v] else None)
    let body = go expr1 [expr2] []
    Translate body targetContext

and Translate expr (targetContext:TargetContext<_,_>) =
    //printfn "%A" expr
    match expr with
    | Patterns.AddressOf expr -> "AdressOf is not suported:" + string expr|> failwith
    | Patterns.AddressSet expr -> "AdressSet is not suported:" + string expr|> failwith
    | Patterns.Application (expr1,expr2) -> translateApplication expr1 expr2 targetContext
    | Patterns.Call (exprOpt,mInfo,args) -> 
        let r,tContext = translateCall exprOpt mInfo args targetContext
        r :> Node<_>,tContext
    | Patterns.Coerce(expr,sType) -> "Coerce is not suported:" + string expr|> failwith
    | Patterns.DefaultValue sType -> "DefaulValue is not suported:" + string expr|> failwith
    | Patterns.FieldGet (exprOpt,fldInfo) -> "FieldGet is not suported:" + string expr|> failwith
    | Patterns.FieldSet (exprOpt,fldInfo,expr) -> "FieldSet is not suported:" + string expr|> failwith
    | Patterns.ForIntegerRangeLoop (i, from, _to, _do) ->
        let r,tContext = translateForIntegerRangeLoop i from _to _do targetContext
        r :> Node<_>, tContext
    | Patterns.IfThenElse (cond, thenExpr, elseExpr) ->
        let r,tContext = translateIf cond thenExpr elseExpr targetContext
        r :> Node<_>, tContext
    | Patterns.Lambda (var,_expr) -> 
        //translateLambda var expr targetContext
        "Lambda is not suported:" + string expr|> failwith
    | Patterns.Let (var, expr, inExpr) ->
        let bName = targetContext.Namer.LetStart var.Name                
        let vDecl = translateBinding var bName expr targetContext
        targetContext.VarDecls.Add vDecl
        targetContext.Namer.LetIn var.Name
        let res,tContext = Translate inExpr targetContext
        let sb = new ResizeArray<_>(tContext.VarDecls |> Seq.cast<Statement<_>>)
        sb.Add (res :?> Statement<_>)
        targetContext.Namer.LetOut()
        if sb.Count > 1
        then new StatementBlock<_>(sb) :> Node<_>
        else res
        , (clearContext targetContext)

    | Patterns.LetRecursive (bindings,expr) -> "LetRecursive is not suported:" + string expr|> failwith
    | Patterns.NewArray(sType,exprs) -> "NewArray is not suported:" + string expr|> failwith
    | Patterns.NewDelegate(sType,vars,expr) -> "NewDelegate is not suported:" + string expr|> failwith
    | Patterns.NewObject(constrInfo,exprs) -> "NewObject is not suported:" + string expr|> failwith
    | Patterns.NewRecord(sType,exprs) -> "NewRecord is not suported:" + string expr|> failwith
    | Patterns.NewTuple(exprs) -> "NewTuple is not suported:" + string expr|> failwith
    | Patterns.NewUnionCase(unionCaseinfo,exprs) -> "NewUnionCase is not suported:" + string expr|> failwith
    | Patterns.PropertyGet(exprOpt,propInfo,exprs) -> 
        let res, tContext = transletaPropGet exprOpt propInfo exprs targetContext
        (res :> Node<_>), tContext
    | Patterns.PropertySet(exprOpt,propInfo,exprs,expr) -> 
        let res,tContext = transletaPropSet exprOpt propInfo exprs expr targetContext
        res :> Node<_>,tContext
    | Patterns.Quote expr -> "Quote is not suported:" + string expr|> failwith
    | Patterns.Sequential(expr1,expr2) -> 
        let res,tContext = translateSeq expr1 expr2 targetContext
        res :> Node<_>,tContext
    | Patterns.TryFinally(tryExpr,finallyExpr) -> "TryFinally is not suported:" + string expr|> failwith
    | Patterns.TryWith(expr1,var1,expr2,var2,expr3) -> "TryWith is not suported:" + string expr|> failwith 
    | Patterns.TupleGet(expr,i) -> "TupleGet is not suported:" + string expr|> failwith
    | Patterns.TypeTest(expr,sType) -> "TypeTest is not suported:" + string expr|> failwith
    | Patterns.UnionCaseTest(expr,unionCaseInfo) -> "UnionCaseTest is not suported:" + string expr|> failwith
    | Patterns.Value(_obj,sType) -> translateValue _obj sType :> Node<_>, targetContext 
    | Patterns.Var var -> translateVar var targetContext :> Node<_>, targetContext
    | Patterns.VarSet(var,expr) -> 
        let res,tContext = translateVarSet var expr targetContext
        res :> Node<_>,tContext
    | Patterns.WhileLoop(condExpr,bodyExpr) -> 
        let r,tContext = translateWhileLoop condExpr bodyExpr targetContext
        r :> Node<_>, tContext
    | other -> "OTHER!!! :" + string other |> failwith
