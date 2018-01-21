namespace Fable.FSharp2Fable

open System
open System.Collections.Generic
#if !FABLE_COMPILER
open System.Reflection
#endif
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices
open Fable
open Fable.AST
open Fable.AST.Fable.Util

type EnclosingModule(entity, isPublic) =
    member val Entity: Fable.Entity = entity
    member val IsPublic: bool = isPublic

type Context =
    { fileName: string
      enclosingModule: EnclosingModule
      scope: (FSharpMemberOrFunctionOrValue option * Fable.Expr) list
      scopedInlines: (FSharpMemberOrFunctionOrValue * FSharpExpr) list
    /// Some expressions that create scope in F# don't do it in JS (like let bindings)
    /// so we need a mutable registry to prevent duplicated var names.
      varNames: HashSet<string>
      typeArgs: (string * FSharpType) list
      decisionTargets: Map<int, FSharpMemberOrFunctionOrValue list * FSharpExpr> option
      genericAvailability: bool
      isDynamicCurriedLambda: bool
      caughtException: Fable.Ident option }
    static member Create(fileName, enclosingModule) =
        { fileName = fileName
          enclosingModule = EnclosingModule(enclosingModule, true)
          scope = []
          scopedInlines = []
          varNames = HashSet()
          typeArgs = []
          decisionTargets = None
          genericAvailability = false
          isDynamicCurriedLambda = false
          caughtException = None }

type IFableCompiler =
    inherit ICompiler
    abstract Transform: Context -> FSharpExpr -> Fable.Expr
    abstract IsReplaceCandidate: FSharpEntity -> bool
    abstract TryGetInternalFile: FSharpEntity -> string option
    abstract GetEntity: FSharpEntity -> Fable.Entity
    abstract GetInlineExpr: FSharpMemberOrFunctionOrValue -> (IDictionary<FSharpMemberOrFunctionOrValue,int> * FSharpExpr)
    abstract AddInlineExpr: string * (IDictionary<FSharpMemberOrFunctionOrValue,int> * FSharpExpr) -> unit
    abstract AddUsedVarName: string -> unit
    abstract ReplacePlugins: (string*IReplacePlugin) list

module Atts =
    let abstractClass = typeof<AbstractClassAttribute>.FullName
    let compiledName = typeof<CompiledNameAttribute>.FullName
    let emit = typeof<Fable.Core.EmitAttribute>.FullName
    let import = typeof<Fable.Core.ImportAttribute>.FullName
    let global_ = typeof<Fable.Core.GlobalAttribute>.FullName
    let erase = typeof<Fable.Core.EraseAttribute>.FullName
    let stringEnum = typeof<Fable.Core.StringEnumAttribute>.FullName
    let passGenerics = typeof<Fable.Core.PassGenericsAttribute>.FullName
    let paramList = typeof<Fable.Core.ParamListAttribute>.FullName

module Helpers =
    let tryBoth (f1: 'a->'b option) (f2: 'a->'b option) (x: 'a) =
        match f1 x with
        | Some _ as res -> res
        | None ->
            match f2 x with
            | Some _ as res -> res
            | None -> None

    let rec nonAbbreviatedType (t: FSharpType) =
        if t.IsAbbreviation then nonAbbreviatedType t.AbbreviatedType else t

    let rec nonAbbreviatedEntity (ent: FSharpEntity) =
        if ent.IsFSharpAbbreviation
        then (nonAbbreviatedType ent.AbbreviatedType).TypeDefinition
        else ent

    // TODO: Report bug in FCS repo, when ent.IsNamespace, FullName doesn't work.
    let getEntityFullName (ent: FSharpEntity) =
        if ent.IsNamespace
        then match ent.Namespace with Some ns -> ns + "." + ent.CompiledName | None -> ent.CompiledName
        else defaultArg ent.TryFullName ent.CompiledName

    let tryFindAtt f (atts: #seq<FSharpAttribute>) =
        atts |> Seq.tryPick (fun att ->
            match (nonAbbreviatedEntity att.AttributeType).TryFullName with
            | Some fullName ->
                if f fullName then Some att else None
            | None -> None)

    let hasAtt name atts =
        atts |> tryFindAtt ((=) name) |> Option.isSome

    let tryDefinition (typ: FSharpType) =
        let typ = nonAbbreviatedType typ
        if typ.HasTypeDefinition then Some typ.TypeDefinition else None

    // Sometimes accessing `EnclosingEntity` throws an error (e.g. compiler generated
    // methods as in #237) so this prevents uncaught exceptions
    let tryEnclosingEntity (meth: FSharpMemberOrFunctionOrValue) =
        try meth.EnclosingEntity
        with _ -> None

    let isModuleMember (meth: FSharpMemberOrFunctionOrValue) =
        match tryEnclosingEntity meth with
        | Some ent -> ent.IsFSharpModule
        | None -> false

    let isInline (meth: FSharpMemberOrFunctionOrValue) =
        match meth.InlineAnnotation with
        | FSharpInlineAnnotation.NeverInline
        | FSharpInlineAnnotation.OptionalInline -> false
        | FSharpInlineAnnotation.PseudoValue
        | FSharpInlineAnnotation.AlwaysInline -> true
        | FSharpInlineAnnotation.AggressiveInline -> failwith "Not Implemented"

    /// .IsPrivate for members of a private module always evaluate to true (see #696)
    /// so we just make all members of a private module public until a proper solution comes in FCS
    let isPublicMethod (meth: FSharpMemberOrFunctionOrValue) =
        if meth.IsCompilerGenerated
        then false
        else
            match tryEnclosingEntity meth with
            | Some ent when ent.Accessibility.IsPrivate -> true
            | _ -> not meth.Accessibility.IsPrivate

    /// .IsPrivate for types of a private module always evaluate to true (see #841)
    /// so we just make all members of a private module public until a proper solution comes in FCS
    let isPublicEntity (ctx: Context) (ent: FSharpEntity) =
        if not ctx.enclosingModule.IsPublic
        then true
        else not ent.RepresentationAccessibility.IsPrivate

    let isUnit (typ: FSharpType) =
        let typ = nonAbbreviatedType typ
        if typ.HasTypeDefinition
        then typ.TypeDefinition.TryFullName = Some "Microsoft.FSharp.Core.Unit"
        else false

    let belongsToInterfaceOrImportedEntity (meth: FSharpMemberOrFunctionOrValue) =
        // TODO: Temporary HACK to fix #577
        if meth.FullName.StartsWith("Fable.Import.Node") then true else
        match tryEnclosingEntity meth with
        | Some ent ->
            meth.IsExplicitInterfaceImplementation
            || ent.IsInterface
            || (ent.Attributes
                |> tryFindAtt (fun name ->
                    name = Atts.import || name = Atts.global_)
                |> Option.isSome)
        | None -> false

    let sameMemberLoc memberLoc1 memberLoc2 =
        match memberLoc1, memberLoc2 with
        | Fable.StaticLoc, Fable.StaticLoc -> true
        | Fable.InstanceLoc, Fable.InstanceLoc -> true
        | Fable.InterfaceLoc _, Fable.InterfaceLoc _ -> true
        | _ -> false

    let makeRange (r: Range.range) = {
        start = { line = r.StartLine; column = r.StartColumn }
        ``end``= { line = r.EndLine; column = r.EndColumn }
    }

    let makeRangeFrom (fsExpr: FSharpExpr) =
        Some (makeRange fsExpr.Range)

    let getEntityLocation (ent: FSharpEntity) =
        match ent.ImplementationLocation with
        | Some loc -> loc
        | None -> ent.DeclarationLocation

    let getMethLocation (meth: FSharpMemberOrFunctionOrValue) =
        match meth.ImplementationLocation with
        | Some loc -> loc
        | None -> meth.DeclarationLocation

    let getUnionCaseIndex fsType unionCaseName =
        match tryDefinition fsType with
        | None ->
            failwithf "Cannot find Type definition for union case %s" unionCaseName
        | Some tdef ->
            tdef.UnionCases
            |> Seq.findIndex (fun uc -> uc.Name = unionCaseName)

    /// Lower first letter if there's no explicit compiled name
    let lowerCaseName (unionCase: FSharpUnionCase) =
        unionCase.Attributes
        |> tryFindAtt ((=) Atts.compiledName)
        |> function
            | Some name -> name.ConstructorArguments.[0] |> snd |> string
            | None -> Naming.lowerFirst unionCase.DisplayName
        |> makeStrConst

    let tryGetInterfaceFromMethod (meth: FSharpMemberOrFunctionOrValue) =
        // Method implementations
        if meth.IsExplicitInterfaceImplementation
        then
            if meth.ImplementedAbstractSignatures.Count > 0
            then
                let x = meth.ImplementedAbstractSignatures.[0].DeclaringType
                if x.HasTypeDefinition then Some x.TypeDefinition else None
            else None
        // Method calls
        else
        match tryEnclosingEntity meth with
        | Some ent when ent.IsInterface -> Some ent
        | _ -> None

    let getMemberLoc (meth: FSharpMemberOrFunctionOrValue) =
        if not meth.IsInstanceMember && not meth.IsImplicitConstructor
        then Fable.StaticLoc
        else tryGetInterfaceFromMethod meth
             |> Option.map (getEntityFullName >> Fable.InterfaceLoc)
             |> Option.defaultValue Fable.InstanceLoc

    let getArgCount (meth: FSharpMemberOrFunctionOrValue) =
        let args = meth.CurriedParameterGroups
        if args.Count = 0 then 0
        elif args.Count = 1 && args.[0].Count = 1 then
            if isUnit args.[0].[0].Type then 0 else 1
        else args |> Seq.sumBy (fun li -> li.Count)

    let getMemberKind (meth: FSharpMemberOrFunctionOrValue) =
        let ent = tryEnclosingEntity meth
        // `.EnclosingEntity` only fails for compiler generated module members
        if ent.IsNone || (ent.Value.IsFSharpModule) then
            if meth.CurriedParameterGroups.Count = 0
                && meth.GenericParameters.Count = 0
                && not meth.IsMutable // Mutable module values are compiled as functions (see #986)
            then Fable.Field
            else Fable.Method
        elif meth.IsImplicitConstructor then Fable.Constructor
        elif meth.IsPropertyGetterMethod && (getArgCount meth) = 0 then Fable.Getter
        elif meth.IsPropertySetterMethod && (getArgCount meth) = 1 then Fable.Setter
        else Fable.Method

    let fullNameAndArgCount (meth: FSharpMemberOrFunctionOrValue) =
        meth.FullName + "(" + (getArgCount meth |> string) + ")"

    // TODO: Check when EnclosingEntity fails. What about interfaces/overrides?
    // TODO: Overloads
    let sanitizeMethodName (meth: FSharpMemberOrFunctionOrValue) =
        match tryEnclosingEntity meth with
        | Some ent ->
            if ent.IsFSharpModule then
                meth.CompiledName
            elif meth.IsOverrideOrExplicitInterfaceImplementation
                || meth.IsInstanceMember && ent.IsInterface then
                meth.DisplayName
            else
                let separator = if meth.IsInstanceMember then "$" else "$$"
                ent.CompiledName + separator + meth.CompiledName
        | None ->
            meth.CompiledName

    let hasRestParams (meth: FSharpMemberOrFunctionOrValue) =
        if meth.CurriedParameterGroups.Count <> 1 then false else
        let args = meth.CurriedParameterGroups.[0]
        args.Count > 0 && args.[args.Count - 1].IsParamArrayArg

    let hasListParam (meth: FSharpMemberOrFunctionOrValue) =
        Seq.tryLast meth.CurriedParameterGroups
        |> Option.bind Seq.tryLast
        |> Option.map (fun lastParam -> hasAtt Atts.paramList lastParam.Attributes)
        |> Option.defaultValue false

    let hasPassGenericsAtt com (ctx: Context) (meth: FSharpMemberOrFunctionOrValue) =
        match hasAtt Atts.passGenerics meth.Attributes with
        | true when hasRestParams meth ->
            Atts.passGenerics + " is not compatible with ParamArrayAttribute"
            |> addError com ctx.fileName (getMethLocation meth |> makeRange |> Some)
            false
        | result -> result

    let removeOmittedOptionalArguments (meth: FSharpMemberOrFunctionOrValue) (args: Fable.Expr list) =
        let rec removeArgs (args: (Fable.Expr*FSharpParameter) list) =
            match args with
            | (arg, p)::rest ->
                if arg.IsNull && p.IsOptionalArg
                then removeArgs rest
                else args
            | _ -> args
        if meth.CurriedParameterGroups.Count <> 1
        then args
        elif meth.CurriedParameterGroups.[0].Count <> List.length args
        then args
        else
            List.zip args (Seq.toList meth.CurriedParameterGroups.[0])
            |> List.rev |> removeArgs |> List.rev |> List.map fst

module Patterns =
    open BasicPatterns
    open Helpers

    let inline (|Rev|) x = List.rev x
    let inline (|AsArray|) x = Array.ofSeq x
    let inline (|LazyValue|) (x: Lazy<'T>) = x.Value
    let inline (|Transform|) (com: IFableCompiler) ctx = com.Transform ctx
    let inline (|FieldName|) (fi: FSharpField) = fi.Name
    let inline (|ExprType|) (expr: Fable.Expr) = expr.Type
    let inline (|EntityKind|) (ent: Fable.Entity) = ent.Kind

    let inline (|NonAbbreviatedType|) (t: FSharpType) =
        nonAbbreviatedType t

    let (|TypeDefinition|_|) (NonAbbreviatedType t) =
        if t.HasTypeDefinition then Some t.TypeDefinition else None

    let (|RefType|_|) = function
        | NonAbbreviatedType(TypeDefinition tdef) as t
            when tdef.TryFullName = Some "Microsoft.FSharp.Core.FSharpRef`1" -> Some t
        | _ -> None

    let (|ListType|_|) = function
        | NonAbbreviatedType(TypeDefinition tdef) as t
            when tdef.TryFullName = Some "Microsoft.FSharp.Collections.FSharpList`1" -> Some t
        | _ -> None

    let (|MaybeWrapped|_|) = function
        // TODO: Ask why application without arguments happen. So far I've seen it
        // to access None or struct values (like the Result type)
        | Application(expr,_,[]) -> Some expr
        // TODO: Ask about this. I've seen it when accessing Result types
        // (applicable to all structs?)
        | AddressOf(expr) -> Some expr
        | _ -> None

    let (|ThisVar|_|) = function
        | BasicPatterns.ThisValue _ -> Some ThisVar
        | BasicPatterns.Value var when
            var.IsMemberThisValue || var.IsConstructorThisValue ->
            Some ThisVar
        | _ -> None

    let (|ForOfLoop|_|) = function
        | Let((_, value),
              Let((_, Call(None, meth, _, [], [])),
                TryFinally(
                  WhileLoop(_,
                    Let((ident, _), body)), _)))
        | Let((_, Call(Some value, meth, _, [], [])),
                TryFinally(
                    WhileLoop(_,
                        Let((ident, _), body)), _))
            when meth.CompiledName = "GetEnumerator" ->
            Some(ident, value, body)
        | _ -> None

    let (|PrintFormat|_|) fsExpr =
        match fsExpr with
        | Let((v,(Call(None,_,_,_,args) as e)),_) when v.IsCompilerGenerated ->
            match List.tryLast args with
            | Some arg ->
                if arg.Type.HasTypeDefinition
                    && arg.Type.TypeDefinition.AccessPath = "Microsoft.FSharp.Core.PrintfModule"
                then Some e
                else None
            | None -> None
        | _ -> None

    let (|JsThis|_|) = function
        | Call(None, jsThis, _, _, [])
            when jsThis.FullName.StartsWith("Fable.Core.JsInterop.jsThis") ->
            Some JsThis
        | _ -> None

    let (|FlattenedApplication|_|) fsExpr =
        let rec flattenApplication typeArgs args = function
            | Application(expr, typeArgs2, args2) ->
                flattenApplication (typeArgs2@typeArgs) (args2@args) expr
            | expr ->
                Some(expr, typeArgs, args)
        match fsExpr with
        | Application(expr, typeArgs, args) ->
            flattenApplication typeArgs args expr
        | _ -> None

    let (|FlattenedLambda|_|) fsExpr =
        // F# compiler puts tuple destructs in between curried arguments
        // E.g `fun (x, y) z -> x + y + z` becomes `(tupledArg) => { var x = tupledArg[0]; var y = tupledArg[1]; return z => x + y + z }`
        // so we need to detect this destructs in order to flatten the lambda
        let rec flattenDestructs tupleDestructs = function
            | Let ((var, (TupleGet(_,_,Value _) as arg)), body) -> flattenDestructs ((var,arg)::tupleDestructs) body
            | e -> tupleDestructs, e
        let rec flattenLambda args tupleDestructs = function
            | Lambda(arg, body) ->
                let tupleDestructs, body =
                    if arg.FullType.IsTupleType && arg.IsCompilerGenerated && arg.CompiledName = "tupledArg"
                    then flattenDestructs tupleDestructs body
                    else tupleDestructs, body
                flattenLambda (arg::args) tupleDestructs body
            | body ->
                if List.isEmpty args
                then None
                else Some(List.rev args, List.rev tupleDestructs, body)
        flattenLambda [] [] fsExpr

    let (|ImmutableBinding|_|) = function
        | Let((var, boundExpr), body) when not var.IsMutable ->
            match boundExpr with
            // This is a bit dangerous if the lambda is referenced multiple times, but when the binding is generated
            // by the compiler (which happens often in pipe chains) this is not usually the case
            | Lambda _ when var.IsCompilerGenerated -> Some((var, boundExpr), body)
            // | Value v as value when not v.IsMutable && not v.IsMemberThisValue -> Some((var, value), body)
            // | Const _ -> Some((var, boundExpr), body)
            | UnionCaseGet(Value v,_,_,_) when not v.IsMutable -> Some((var, boundExpr), body)
            | TupleGet(_,_,Value v) when not v.IsMutable -> Some((var, boundExpr), body)
            | FSharpFieldGet(Some(Value v),_,fi) when not v.IsMutable && not fi.IsMutable -> Some((var, boundExpr), body)
            | _ -> None
        | _ -> None

    /// This matches the boilerplate F# compiler generates for methods
    /// like Dictionary.TryGetValue (see #154)
    let (|TryGetValue|_|) = function
        | Let((outArg1, (DefaultValue _ as def)),
                NewTuple(_, [Call(callee, meth, typArgs, methTypArgs,
                                    [arg; AddressOf(Value outArg2)]); Value outArg3]))
            when outArg1 = outArg2 && outArg1 = outArg3 ->
            Some (callee, meth, typArgs, methTypArgs, [arg; def])
        | _ -> None

    /// This matches the boilerplate generated to wrap .NET events from F#
    let (|CreateEvent|_|) = function
        | Call(Some(Call(None, createEvent,_,_,
                        [Lambda(_eventDelegate, Call(Some callee, addEvent,[],[],[Value _eventDelegate']));
                         Lambda(_eventDelegate2, Call(Some _callee2, _removeEvent,[],[],[Value _eventDelegate2']));
                         Lambda(_callback, NewDelegate(_, Lambda(_delegateArg0, Lambda(_delegateArg1, Application(Value _callback',[],[Value _delegateArg0'; Value _delegateArg1'])))))])),
                meth, typArgs, methTypArgs, args)
                when createEvent.FullName = "Microsoft.FSharp.Core.CompilerServices.RuntimeHelpers.CreateEvent" ->
            let eventName = addEvent.CompiledName.Replace("add_","")
            Some (callee, eventName, meth, typArgs, methTypArgs, args)
        | _ -> None

    /// This matches the boilerplate generated to check an array's length
    /// when pattern matching
    let (|CheckArrayLength|_|) = function
        | IfThenElse
            (ILAsm ("[AI_ldnull; AI_cgt_un]",[],[matchValue]),
             Call(None,_op_Equality,[],[_typeInt],
                [ILAsm ("[I_ldlen; AI_conv DT_I4]",[],[_matchValue2])
                 Const (length,_typeInt2)]),
             Const (_falseConst,_typeBool)) -> Some (matchValue, length, _typeInt2)
        | _ -> None

    let (|NumberKind|_|) = function
        | "System.SByte" -> Some Int8
        | "System.Byte" -> Some UInt8
        | "System.Int16" -> Some Int16
        | "System.UInt16" -> Some UInt16
        | "System.Int32" -> Some Int32
        | "System.UInt32" -> Some UInt32
        | "System.Single" -> Some Float32
        | "System.Double" -> Some Float64
        // Units of measure
        | Naming.StartsWith "Microsoft.FSharp.Core.sbyte" _ -> Some Int8
        | Naming.StartsWith "Microsoft.FSharp.Core.int16" _ -> Some Int16
        | Naming.StartsWith "Microsoft.FSharp.Core.int" _ -> Some Int32
        | Naming.StartsWith "Microsoft.FSharp.Core.float32" _ -> Some Float32
        | Naming.StartsWith "Microsoft.FSharp.Core.float" _ -> Some Float64
        | _ -> None

    let (|ExtendedNumberKind|_|) = function
        | "System.Int64" -> Some Int64
        | "System.UInt64" -> Some UInt64
        | "System.Decimal" -> Some Decimal
        | "System.Numerics.BigInteger" -> Some BigInt
        // Units of measure
        | Naming.StartsWith "Microsoft.FSharp.Core.int64" _ -> Some Int64
        | Naming.StartsWith "Microsoft.FSharp.Core.decimal" _ -> Some Decimal
        | _ -> None

    let (|OptionUnion|ListUnion|ErasedUnion|StringEnum|OtherType|) (NonAbbreviatedType typ: FSharpType) =
        match tryDefinition typ with
        | None -> OtherType true // default to unions as arrays
        | Some tdef ->
            match defaultArg tdef.TryFullName tdef.CompiledName with
            | "Microsoft.FSharp.Core.FSharpOption`1" -> OptionUnion
            | "Microsoft.FSharp.Collections.FSharpList`1" -> ListUnion
            | _ ->
                tdef.Attributes
                |> Seq.choose (fun att -> (nonAbbreviatedEntity att.AttributeType).TryFullName)
                |> Seq.tryPick (fun name ->
                    if name = Atts.erase then Some ErasedUnion
                    elif name = Atts.stringEnum then Some StringEnum
                    else None)
                |> Option.defaultWith (fun () ->
                    let hasCasewithDataFields =
                        tdef.UnionCases
                        |> Seq.exists (fun uci -> uci.UnionCaseFields.Count > 0)
                    OtherType hasCasewithDataFields)

    let (|Switch|_|) fsExpr =
        let isStringOrNumber (NonAbbreviatedType typ) =
            if not typ.HasTypeDefinition then false else
            match typ.TypeDefinition.TryFullName with
            | Some("System.String") -> true
            | Some(NumberKind _) -> true
            | _ when typ.TypeDefinition.IsEnum -> true
            | _ -> false
        let rec makeSwitch size map matchValue e =
            match e with
            | IfThenElse(Call(None,op_Equality,[],_,[Value var; Const(case,_)]), DecisionTreeSuccess(idx, bindings), elseExpr)
                    when op_Equality.CompiledName.Equals("op_Equality") ->
                let case =
                    match case with
                    | :? int as i -> Choice1Of2 i |> Some
                    | :? string as s -> Choice2Of2 s |> Some
                    | _ -> None
                match case, matchValue with
                | Some case, Some matchValue when matchValue.Equals(var) ->
                    Some(matchValue,false,idx,bindings,case,elseExpr)
                | Some case, None when isStringOrNumber var.FullType && not var.IsMemberThisValue && not(isInline var) ->
                    Some(var,false,idx,bindings,case,elseExpr)
                | _ -> None
            | IfThenElse(UnionCaseTest(Value var,typ,case), DecisionTreeSuccess(idx, bindings), elseExpr) ->
                let case = getUnionCaseIndex typ case.Name |> Choice1Of2
                match matchValue with
                | Some matchValue when matchValue.Equals(var) ->
                    Some(matchValue,true,idx,bindings,case,elseExpr)
                | None when not var.IsMemberThisValue && not(isInline var) ->
                    match typ with
                    | OptionUnion | ListUnion | ErasedUnion | StringEnum -> None
                    | OtherType _ -> Some(var,true,idx,bindings,case,elseExpr)
                | _ -> None
            | _ -> None
            |> function
                | Some(matchValue,isUnionType,idx,bindings,case,elseExpr) ->
                    let map =
                        match Map.tryFind idx map with
                        | None -> Map.add idx (bindings, [case]) map |> Some
                        | Some([],cases) when List.isEmpty bindings -> Map.add idx (bindings, cases@[case]) map |> Some
                        | Some _ -> None // Multiple case with multiple var bindings, cannot optimize
                    match map, elseExpr with
                    | Some map, DecisionTreeSuccess(idx, bindings) ->
                        Some(matchValue, isUnionType, size + 1, map, (idx, bindings))
                    | Some map, elseExpr -> makeSwitch (size + 1) map (Some matchValue) elseExpr
                    | None, _ -> None
                | None -> None
        match fsExpr with
        | DecisionTree(decisionExpr, decisionTargets) ->
            match makeSwitch 0 Map.empty None decisionExpr with
            // For small sizes it's better not to convert to switch so
            // the match is still a expression and not a statement
            | Some(matchValue, isUnionType, size, cases, defaultCase) when size > 3 ->
                Some(matchValue, isUnionType, cases, defaultCase, decisionTargets)
            | _ -> None
        | _ -> None

    let (|ContainsAtt|_|) (name: string) (atts: #seq<FSharpAttribute>) =
        atts |> tryFindAtt ((=) name) |> Option.map (fun att ->
            att.ConstructorArguments |> Seq.map snd |> Seq.toList)

module Types =
    open Helpers
    open Patterns

    let rec isAttributeEntity (ent: FSharpEntity) =
        match ent.BaseType with
        | Some (NonAbbreviatedType t) when t.HasTypeDefinition ->
            match t.TypeDefinition.TryFullName with
            | Some "System.Attribute" -> true
            | _ -> isAttributeEntity t.TypeDefinition
        | _ -> false

    // Some attributes (like ComDefaultInterface) will throw an exception
    // when trying to access ConstructorArguments
    let makeDecorator (att: FSharpAttribute) =
        try
            let args = att.ConstructorArguments |> Seq.map snd |> Seq.toList
            let fullName =
                let fullName = getEntityFullName att.AttributeType
                if fullName.EndsWith ("Attribute")
                then fullName.Substring (0, fullName.Length - 9)
                else fullName
            Fable.Decorator(fullName, args) |> Some
        with _ ->
            None

    let rec getFunctionGenericArgs (acc: FSharpType list) (typeArgs: (string*FSharpType) list)
                                    isFunctionType (typ: FSharpType): FSharpType list =
        if isFunctionType then
            let genArg0 = nonAbbreviatedType typ.GenericArguments.[0]
            let genArg1 = nonAbbreviatedType typ.GenericArguments.[1]
            getFunctionGenericArgs (genArg0::acc) typeArgs genArg1.IsFunctionType genArg1
        elif typ.IsGenericParameter then
            typeArgs |> List.tryFind (fun (name,_) -> name = typ.GenericParameter.Name)
            |> function
                | Some (_, typ2) when typ2.IsFunctionType ->
                    getFunctionGenericArgs [] typeArgs true typ2
                | _ -> typ::acc
        else typ::acc

    let rec makeTypeFromDef (com: IFableCompiler) typeArgs (tdef: FSharpEntity)
                        (genArgs: seq<FSharpType>) =
        let tdef = nonAbbreviatedEntity tdef
        let fullName = getEntityFullName tdef
        // printfn "makeTypeFromDef %s" fullName
        // Array
        if tdef.IsArrayType
        then Fable.Array(Seq.head genArgs |> makeType com typeArgs)
        // Enum
        elif tdef.IsEnum
        then Fable.Enum fullName
        // Delegate
        elif tdef.IsDelegate
        then
            if fullName.StartsWith("System.Action")
            then
                if Seq.length genArgs = 1
                then [Seq.head genArgs |> makeType com typeArgs], Fable.Unit, false
                else [Fable.Unit], Fable.Unit, false
                |> Fable.Function
            elif fullName.StartsWith("System.Func")
            then
                match Seq.length genArgs with
                | 0 -> [Fable.Unit], Fable.Unit, false
                | 1 -> [Fable.Unit], Seq.head genArgs |> makeType com typeArgs, false
                | c -> Seq.take (c-1) genArgs |> Seq.map (makeType com typeArgs) |> Seq.toList,
                        Seq.last genArgs |> makeType com typeArgs, false
                |> Fable.Function
            else
            try
                let argTypes =
                    tdef.FSharpDelegateSignature.DelegateArguments
                    |> Seq.map (snd >> makeType com typeArgs) |> Seq.toList
                let retType =
                    makeType com typeArgs tdef.FSharpDelegateSignature.DelegateReturnType
                Fable.Function(argTypes, retType, false)
            with _ -> Fable.Function([Fable.Any], Fable.Any, false)
        // Object
        elif fullName = "System.Object"
        then Fable.Any
        else
        match fullName with
        | "System.Boolean" -> Fable.Boolean
        | "System.Char" -> Fable.Char
        | "System.String" | "System.Guid" -> Fable.String
        | "Microsoft.FSharp.Core.Unit" -> Fable.Unit
        | "Microsoft.FSharp.Core.FSharpOption`1" ->
            let t = Seq.tryHead genArgs |> Option.map (makeType com typeArgs)
            Fable.Option(defaultArg t Fable.Any)
        | "System.Collections.Generic.List`1" ->
            let t = Seq.tryHead genArgs |> Option.map (makeType com typeArgs)
            Fable.Array(defaultArg t Fable.Any)
        | NumberKind kind -> Fable.Number kind
        | ExtendedNumberKind kind -> Fable.ExtendedNumber kind
        | _ ->
            // Check erased types
            tdef.Attributes
            |> Seq.choose (fun att -> (nonAbbreviatedEntity att.AttributeType).TryFullName)
            |> Seq.tryPick (fun name ->
                if name = Atts.stringEnum
                then Some Fable.String
                elif name = Atts.erase
                then Some Fable.Any
                else None)
            |> Option.defaultWith (fun () -> // Declared Type
                Fable.DeclaredType(com.GetEntity tdef,
                    genArgs |> Seq.map (makeType com typeArgs) |> Seq.toList))

    and makeType (com: IFableCompiler) typeArgs (NonAbbreviatedType t) =
        // printfn "makeType %O" t
        let makeGenArgs (genArgs: #seq<FSharpType>) =
            Seq.map (makeType com typeArgs) genArgs |> Seq.toList
        let resolveGenParam (genParam: FSharpGenericParameter) =
            match typeArgs |> List.tryFind (fun (name,_) -> name = genParam.Name) with
            // Clear typeArgs to prevent infinite recursion
            | Some (_,typ) -> makeType com [] typ
            | None -> Fable.GenericParam genParam.Name
        // Generic parameter (try to resolve for inline functions)
        if t.IsGenericParameter
        then resolveGenParam t.GenericParameter
        // Tuple
        elif t.IsTupleType
        then Fable.Tuple(makeGenArgs t.GenericArguments)
        // Funtion
        elif t.IsFunctionType
        then
            let gs = getFunctionGenericArgs [] typeArgs true t
            let argTypes = List.rev gs.Tail |> List.map (makeType com typeArgs)
            let returnType = makeType com typeArgs gs.Head
            Fable.Function(argTypes, returnType, true)
        elif t.HasTypeDefinition
        then makeTypeFromDef com typeArgs t.TypeDefinition t.GenericArguments
        else Fable.Any // failwithf "Unexpected non-declared F# type: %A" t

    let getBaseClass (com: IFableCompiler) (tdef: FSharpEntity) =
        match tdef.BaseType with
        | Some(TypeDefinition tdef) when tdef.TryFullName <> Some "System.Object" ->
            let typeRef = makeTypeFromDef com [] tdef [] |> makeNonGenTypeRef com
            Some (getEntityFullName tdef, typeRef)
        | _ -> None

    let rec getOwnAndInheritedFsharpMembers (tdef: FSharpEntity) = seq {
        yield! tdef.TryGetMembersFunctionsAndValues
        match tdef.BaseType with
        | Some(TypeDefinition baseDef) when tdef.TryFullName <> Some "System.Object" ->
            yield! getOwnAndInheritedFsharpMembers baseDef
        | _ -> ()
    }

    let makeMethodFrom com name kind loc argTypes returnType originalTyp overloadIndex
                       (meth: FSharpMemberOrFunctionOrValue) =
        Fable.Member(name, kind, loc, argTypes, returnType,
            originalType = originalTyp,
            genParams = (meth.GenericParameters |> Seq.map (fun x -> x.Name) |> Seq.toList),
            decorators = (meth.Attributes |> Seq.choose makeDecorator |> Seq.toList),
            isMutable = meth.IsMutable,
            ?overloadIndex = overloadIndex,
            hasRestParams = hasRestParams meth)

    let getArgTypes com (args: IList<IList<FSharpParameter>>) =
        // FSharpParameters don't contain the `this` arg
        Seq.concat args
        // The F# compiler "untuples" the args in methods
        |> Seq.map (fun x -> makeType com [] x.Type)
        |> Seq.toList

    let makeOriginalCurriedType com (args: IList<IList<FSharpParameter>>) returnType =
        let tys = args |> Seq.map (fun tuple ->
            let tuple = tuple |> Seq.map (fun t -> makeType com [] t.Type)
            match List.ofSeq tuple with
            | [singleArg] -> singleArg
            | args -> Fable.Tuple(args) )
        Seq.append tys [returnType] |> Seq.reduceBack (fun a b -> Fable.Function([a], b, true))

    let getMembers com (tdef: FSharpEntity) =
        let isAbstract =
            hasAtt Atts.abstractClass tdef.Attributes
        let isDefaultImplementation (x: FSharpMemberOrFunctionOrValue) =
            isAbstract && x.IsOverrideOrExplicitInterfaceImplementation && not x.IsExplicitInterfaceImplementation
        // F# allows abstract method syntax in non-abstract classes
        // if there's a default implementation (see #701)
        let isFakeAbstractMethod (x: FSharpMemberOrFunctionOrValue) =
            not isAbstract && not tdef.IsInterface && x.IsDispatchSlot
        let existsInterfaceMember name =
            tdef.AllInterfaces
            |> Seq.exists (fun ifc ->
                if not ifc.HasTypeDefinition then false else
                ifc.TypeDefinition.TryGetMembersFunctionsAndValues
                |> Seq.exists (fun m -> m.DisplayName = name))
        let members =
            tdef.TryGetMembersFunctionsAndValues
            |> Seq.filter (fun x ->
                // Discard overrides in abstract classes (that is, default implementations)
                // to prevent confusing them with overloads (see #505)
                not(isDefaultImplementation x)
                // Property members that are no getter nor setter don't actually get implemented
                && not(x.IsProperty && not(x.IsPropertyGetterMethod || x.IsPropertySetterMethod))
                && not(isFakeAbstractMethod x))
            |> Seq.map (fun meth -> sanitizeMethodName meth, getMemberKind meth, getMemberLoc meth, meth)
            |> Seq.toArray
        let getMembers' loc (tdef: FSharpEntity) =
            members
            |> Seq.filter (fun (_, _, mloc, _) -> sameMemberLoc loc mloc)
            |> Seq.groupBy (fun (name, kind, _, _) -> name, kind)
            |> Seq.collect (fun ((name, kind), AsArray members) ->
                let isOverloaded =
                    if tdef.IsInterface then false else
                    match loc with
                    | Fable.InterfaceLoc _ -> false
                    | Fable.InstanceLoc -> members.Length > 1 || existsInterfaceMember name
                    | Fable.StaticLoc -> members.Length > 1
                members |> Array.mapi (fun i (_, _, loc, meth) ->
                    let argTypes = getArgTypes com meth.CurriedParameterGroups
                    let returnType = makeType com [] meth.ReturnParameter.Type
                    let originalTyp = makeOriginalCurriedType com meth.CurriedParameterGroups returnType
                    let overloadIndex = if isOverloaded then Some i else None
                    makeMethodFrom com name kind loc argTypes returnType originalTyp overloadIndex meth
            ))
            |> Seq.toList
        let instanceMembers = getMembers' Fable.InstanceLoc tdef
        let staticMembers = getMembers' Fable.StaticLoc tdef
        let interfaceMembers = getMembers' (Fable.InterfaceLoc "") tdef
        instanceMembers@interfaceMembers@staticMembers

    /// Don't use this method directly, use IFableCompiler.GetEntity instead
    let makeEntity (com: IFableCompiler) (tdef: FSharpEntity): Fable.Entity =
        let makeFields (tdef: FSharpEntity) =
            tdef.FSharpFields
            |> Seq.map (fun x -> x.Name, makeType com [] x.FieldType)
            |> Seq.toList
        let makeProperties (tdef: FSharpEntity) =
            tdef.TryGetMembersFunctionsAndValues
            |> Seq.choose (fun x ->
                if not x.IsPropertyGetterMethod
                    || x.IsExplicitInterfaceImplementation
                then None
                else
                    match makeType com [] x.FullType with
                    | Fable.Function(_, returnType, _) ->
                        Some(x.DisplayName, returnType)
                    | _ -> None)
            |> Seq.toList
        let makeCases (tdef: FSharpEntity) =
            tdef.UnionCases |> Seq.map (fun uci ->
                let name =
                    uci.Attributes
                    |> tryFindAtt ((=) Atts.compiledName)
                    |> function
                        | Some name -> name.ConstructorArguments.[0] |> snd |> string
                        | None -> uci.Name
                name, [for fi in uci.UnionCaseFields do yield makeType com [] fi.FieldType])
            |> Seq.toList
        let getKind () =
            if tdef.IsInterface then Fable.Interface
            elif tdef.IsFSharpUnion then makeCases tdef |> Fable.Union
            elif tdef.IsFSharpRecord || tdef.IsValueType then makeFields tdef |> Fable.Record
            elif tdef.IsFSharpExceptionDeclaration then makeFields tdef |> Fable.Exception
            elif tdef.IsFSharpModule || tdef.IsNamespace then Fable.Module
            else Fable.Class(getBaseClass com tdef, makeProperties tdef)
        let genParams =
            tdef.GenericParameters |> Seq.map (fun x -> x.Name) |> Seq.toList
        let infcs =
            tdef.DeclaredInterfaces
            |> Seq.map (fun x -> getEntityFullName x.TypeDefinition)
            |> Seq.filter (Naming.ignoredInterfaces.Contains >> not)
            |> Seq.distinct
            |> Seq.toList
        let decs =
            tdef.Attributes
            |> Seq.choose makeDecorator
            |> Seq.toList
        Fable.Entity (lazy getKind(), com.TryGetInternalFile tdef,
            getEntityFullName tdef, lazy getMembers com tdef, genParams, infcs, decs)

    let inline (|FableEntity|) (com: IFableCompiler) e = com.GetEntity e
    let inline (|FableType|) com (ctx: Context) t = makeType com ctx.typeArgs t

module Identifiers =
    open Types

    let bindExpr (ctx: Context) (fsRef: FSharpMemberOrFunctionOrValue) expr =
        { ctx with scope = (Some fsRef, expr)::ctx.scope}

    let private bindIdentPrivate (com: IFableCompiler) (ctx: Context) typ
                  (fsRef: FSharpMemberOrFunctionOrValue option) force name =
        let sanitizedName = name |> Naming.sanitizeIdent (fun x ->
            not force && ctx.varNames.Contains x)
        ctx.varNames.Add sanitizedName |> ignore
        // We still need to keep track of all used variable names in the file
        // so they're not used for imports
        com.AddUsedVarName sanitizedName
        let ident = Fable.Ident(sanitizedName, typ)
        let identValue = Fable.Value (Fable.IdentValue ident)
        { ctx with scope = (fsRef, identValue)::ctx.scope}, ident

    let bindIdentWithExactName com ctx typ fsRef name =
        bindIdentPrivate com ctx typ fsRef true name

    /// Make a sanitized identifier from a tentative name
    let bindIdent com ctx typ fsRef tentativeName =
        bindIdentPrivate com ctx typ fsRef false tentativeName

    /// Sanitize F# identifier and create new context
    let bindIdentFrom com ctx (fsRef: FSharpMemberOrFunctionOrValue): Context*Fable.Ident =
        bindIdent com ctx (makeType com ctx.typeArgs fsRef.FullType) (Some fsRef) fsRef.CompiledName

    let (|BindIdent|) = bindIdentFrom

    /// Get corresponding identifier to F# value in current scope
    let tryGetBoundExpr (ctx: Context) (fsRef: FSharpMemberOrFunctionOrValue) =
        ctx.scope
        |> List.tryFind (fst >> function Some fsRef' -> obj.Equals(fsRef, fsRef') | None -> false)
        |> function
            | Some(_, boundExpr) -> Some boundExpr
            | None -> None

module Util =
    open Helpers
    open Patterns
    open Types
    open Identifiers

    let countRefs fsExpr (vars: #seq<FSharpMemberOrFunctionOrValue>) =
        let varsDic = Dictionary()
        for var in vars do varsDic.Add(var, 0)
        let rec countRefs = function
            | BasicPatterns.Value v when not v.IsModuleValueOrMember ->
                match varsDic.TryGetValue(v) with
                | true, count -> varsDic.[v] <- count + 1
                | false, _ -> ()
            | expr -> expr.ImmediateSubExpressions |> Seq.iter countRefs
        countRefs fsExpr
        varsDic

    let makeLambdaArgs com ctx (vars: FSharpMemberOrFunctionOrValue list) =
        let ctx, args =
            ((ctx, []), vars)
            ||> List.fold (fun (ctx, accArgs) var ->
                let newContext, arg = bindIdentFrom com ctx var
                newContext, arg::accArgs)
        ctx, List.rev args

    let bindMemberArgs com ctx passGenerics (args: FSharpMemberOrFunctionOrValue list list) =
        // To prevent name clashes in JS create a scope for members
        // where variables must always have a unique name
        let ctx = { ctx with varNames = HashSet(ctx.varNames) }
        let ctx, args =
            (args, (ctx, [])) ||> List.foldBack (fun tupledArg (ctx, accArgs) ->
                // The F# compiler "untuples" the args in methods
                let ctx, untupledArg = makeLambdaArgs com ctx tupledArg
                ctx, untupledArg@accArgs)
        /// TODO: Remove unit arg if single or with this arg
        if passGenerics
        then { ctx with genericAvailability=true }, args @ [makeIdent Naming.genArgsIdent]
        else ctx, args

    let makeTryCatch com ctx (fsExpr: FSharpExpr) (Transform com ctx body) catchClause finalBody =
        let catchClause =
            match catchClause with
            | Some (BindIdent com ctx (catchContext, catchVar), catchBody) ->
                // Add caughtException to context so it can be retrieved if `reraise` is used
                let catchContext = { catchContext with caughtException = Some catchVar }
                Some (catchVar, com.Transform catchContext catchBody)
            | None -> None
        let finalizer =
            match finalBody with
            | Some (Transform com ctx finalBody) -> Some finalBody
            | None -> None
        Fable.TryCatch (body, catchClause, finalizer, makeRangeFrom fsExpr)

    let makeGetFrom r typ callee propExpr =
        Fable.Apply (callee, [propExpr], Fable.ApplyGet, typ, r)

    // This method doesn't work, the arguments don't keep the attributes
//    let hasRestParams (args: FSharpMemberOrFunctionOrValue list list) =
//        match args with
//        | [args] when args.Length > 0 ->
//            tryFindAtt ((=) "ParamArray") (Seq.last args).Attributes
//            |> Option.isSome
//        | _ -> false

    let buildApplyInfo com (ctx: Context) r typ ownerType ownerFullName methName methKind
            (atts, typArgs, methTypArgs, methArgTypes) (callee, args): Fable.ApplyInfo =
        {
            ownerType = ownerType
            ownerFullName = Naming.replaceGenericArgsCount(ownerFullName, "")
            methodName = methName
            methodKind = methKind
            range = r
            fileName = ctx.fileName
            callee = callee
            args = args
            returnType = typ
            decorators = atts |> Seq.choose makeDecorator |> Seq.toList
            calleeTypeArgs = typArgs |> List.map (makeType com ctx.typeArgs)
            methodTypeArgs = methTypArgs |> List.map (makeType com ctx.typeArgs)
            methodArgTypes = methArgTypes
            genericAvailability = ctx.genericAvailability
            caughtException = ctx.caughtException
        }

    let buildApplyInfoFrom com (ctx: Context) r typ
            (typArgs, methTypArgs, methArgTypes)
            (callee, args) (owner: FSharpEntity option)
            (meth: FSharpMemberOrFunctionOrValue)
            : Fable.ApplyInfo =
        let ownerType, ownerFullName =
            match owner with
            | Some ent -> makeTypeFromDef com ctx.typeArgs ent [], getEntityFullName ent
            | None -> Fable.Any, "System.Object"
        buildApplyInfo com ctx r typ ownerType ownerFullName
            (Naming.removeGetSetPrefix meth.CompiledName) (getMemberKind meth)
            (meth.Attributes, typArgs, methTypArgs, methArgTypes) (callee, args)

    let tryPlugin (com: IFableCompiler) (info: Fable.ApplyInfo) =
        com.ReplacePlugins
        |> Plugins.tryPlugin info.range (fun p -> p.TryReplace com info)

    let (|Plugin|_|) (com: IFableCompiler) (info: Fable.ApplyInfo) (_: FSharpMemberOrFunctionOrValue) =
        tryPlugin com info

    let (|EmitReplacement|_|) (com: IFableCompiler) (info: Fable.ApplyInfo) (_: FSharpMemberOrFunctionOrValue) =
        let fullName = info.ownerFullName + "." + info.methodName
        match Map.tryFind fullName com.Options.emitReplacements with
        | Some replacement ->
            let args =
                match info.callee with
                | Some c -> c::info.args
                | None -> info.args
            makeEmit info.range info.returnType args replacement |> Some
        | None -> None

    let tryReplace (com: IFableCompiler) ctx (ent: FSharpEntity option) (info: Fable.ApplyInfo) =
        let isInterface = function
            | Fable.DeclaredType(ent, _) when ent.Kind = Fable.Interface -> true
            | _ -> false
        match ent with
        | Some ent when com.IsReplaceCandidate ent ->
            match Replacements.tryReplace com info with
            | Some _ as repl -> repl
            | None when isInterface info.ownerType -> None
            | None ->
                sprintf "Cannot find replacement for %s::%s" info.ownerFullName info.methodName
                |> addErrorAndReturnNull com ctx.fileName info.range |> Some
        | _ -> None

    let (|Replaced|_|) (com: IFableCompiler) ctx owner i (_: FSharpMemberOrFunctionOrValue) =
        tryReplace com ctx owner i

    let (|ResolveGeneric|) genArgs (t: FSharpType) =
        if not t.IsGenericParameter then t else
        let genParam = t.GenericParameter
        genArgs |> List.tryPick (fun (name,t) ->
            if genParam.Name = name then Some t else None)
        // TODO: Throw error if generic cannot be resolved?
        |> Option.defaultValue t

    let matchGenericParams ctx (meth: FSharpMemberOrFunctionOrValue) (typArgs, methTypArgs) =
        ([], meth.GenericParameters, typArgs@methTypArgs)
        |||> Seq.fold2 (fun acc genPar (ResolveGeneric ctx.typeArgs t) -> (genPar.Name, t)::acc)
        |> List.rev

#if !FABLE_COMPILER
    let getEmitter =
        let cache = Dictionary<string, obj>()
        fun (tdef: FSharpEntity) ->
            cache.GetOrAdd(tdef.QualifiedName, fun _ ->
                let filePath = tdef.Assembly.FileName.Value
                let assembly = Reflection.loadAssembly filePath
                let typ = Reflection.getTypes assembly |> Seq.find (fun x ->
                    x.AssemblyQualifiedName = tdef.QualifiedName)
                System.Activator.CreateInstance(typ))
#endif

    let emittedGenericArguments com (ctx: Context) r meth (typArgs, methTypArgs)
                                macro (args: Fable.Expr list) =
        let mutable extraArgs = []
        let addExtraArg arg =
            let pos = args.Length + extraArgs.Length
            extraArgs <- arg::extraArgs
            "$" + string pos
        // Trick to replace reference to generic arguments: $'T
        if Naming.hasGenericPlaceholder macro
        then
            let genArgs = matchGenericParams ctx meth (typArgs, methTypArgs) |> Map
            let genInfo = { makeGeneric=false; genericAvailability=ctx.genericAvailability }
            Naming.replaceGenericPlaceholder (macro, fun m ->
                match genArgs.TryFind m with
                | Some t ->
                    makeType com ctx.typeArgs t |> makeTypeRef com genInfo |> addExtraArg
                | None ->
                    sprintf "Couldn't find generic argument %s requested by Emit expression: %s"
                        m macro
                    |> addWarning com ctx.fileName r
                    m)
        else macro
        |> fun macro -> macro, args@(List.rev extraArgs)

    let (|Erased|_|) com (ctx: Context) r typ (owner: FSharpEntity option)
                    (callee, args) (meth: FSharpMemberOrFunctionOrValue) =
        match owner with
        | Some owner ->
            match owner.Attributes with
            | ContainsAtt Atts.erase _attArgs ->
                match callee with
                | Some callee ->
                    let methName = meth.DisplayName
                    match getMemberKind meth with
                    | Fable.Getter | Fable.Field ->
                        makeGetFrom r typ callee (makeStrConst methName)
                    | Fable.Setter ->
                        Fable.Set (callee, Some (makeStrConst methName), List.head args, r)
                    | Fable.Method ->
                        let m = makeGet r Fable.Any callee (makeStrConst methName)
                        Fable.Apply(m, args, Fable.ApplyMeth, typ, r)
                    | Fable.Constructor ->
                        "Erased type cannot have constructors"
                        |> addErrorAndReturnNull com ctx.fileName r
                    |> Some
                | None ->
                    "Cannot call a static method of an erased type: " + meth.DisplayName
                    |> addErrorAndReturnNull com ctx.fileName r |> Some
            | _ -> None
        | None -> None

    let (|Emitted|_|) com ctx r typ i (typArgs, methTypArgs) (callee, args)
                        (meth: FSharpMemberOrFunctionOrValue) =
        match meth.Attributes with
        | ContainsAtt Atts.emit attArgs ->
            match attArgs with
            | [:? string as macro] ->
                let args =
                    match callee with
                    | None -> args
                    | Some c -> c::args
                let macro, args =
                    emittedGenericArguments com ctx r meth (typArgs, methTypArgs) macro args
                Fable.Apply(Fable.Emit(macro) |> Fable.Value, args, Fable.ApplyMeth, typ, r) |> Some
#if !FABLE_COMPILER
            | (:? FSharpType as emitFsType)::(:? string as emitMethName)::extraArg
                when emitFsType.HasTypeDefinition ->
                try
                    let emitInstance = getEmitter emitFsType.TypeDefinition
                    let emitMeth = emitInstance.GetType().GetTypeInfo().GetMethod(emitMethName)
                    let args: obj[] =
                        match extraArg with
                        | [extraArg] -> [|com; i; extraArg|]
                        | _ -> [|com; i|]
                    emitMeth.Invoke(emitInstance, args) |> unbox |> Some
                with
                | ex -> let exMsg = if ex.GetType() = typeof<TargetInvocationException>
                                    then ex.InnerException.Message else ex.Message
                        sprintf "Error when invoking %s.%s"
                            emitFsType.TypeDefinition.DisplayName emitMethName
                        |> attachRange r |> fun msg -> Exception(msg + ": " + exMsg, ex) |> raise
#endif
            | _ -> "EmitAttribute must receive a string or Type argument" |> attachRange r |> failwith
        | _ -> None

    let (|Imported|_|) com ctx r typ i (typArgs, methTypArgs) (args: Fable.Expr list)
                        (meth: FSharpMemberOrFunctionOrValue) =
        meth.Attributes
        |> Seq.choose makeDecorator
        |> tryImported (lazy sanitizeMethodName meth)
        |> function
            | Some expr ->
                match meth with
                // Allow combination of Import and Emit attributes
                | Emitted com ctx r typ i (typArgs, methTypArgs) (None, expr::args) emitted ->
                    emitted
                | _ ->
                    match getMemberKind meth with
                    | Fable.Getter | Fable.Field -> expr
                    | Fable.Setter -> Fable.Set (expr, None, args.Head, r)
                    | Fable.Constructor
                    | Fable.Method -> Fable.Apply(expr, args, Fable.ApplyMeth, typ, r)
                |> Some
            | None -> None

    let (|Inlined|_|) (com: IFableCompiler) (ctx: Context) r (typArgs, methTypArgs)
                      (callee, args) (meth: FSharpMemberOrFunctionOrValue) =
        if not(isInline meth)
        then None
        else
            let argIdents, fsExpr = com.GetInlineExpr meth
            let args = match callee with Some c -> c::args | None -> args
            let ctx, assignments, _ =
                ((ctx, [], 0), argIdents, args)
                |||> Seq.fold2 (fun (ctx, assignments, idx) (KeyValue(argIdent, refCount)) arg ->
                    // If an expression is referenced more than once, assign it
                    // to a temp var to prevent multiple evaluations
                    if refCount > 1 && hasDoubleEvalRisk arg then
                        let tmpVar = com.GetUniqueVar() |> makeIdent
                        let tmpVarExp = Fable.Value(Fable.IdentValue tmpVar)
                        let assign = Fable.VarDeclaration(tmpVar, arg, false, None)
                        let ctx = { ctx with scope = (Some argIdent, tmpVarExp)::ctx.scope }
                        ctx, (assign::assignments), (idx + 1)
                    else
                        let ctx = { ctx with scope = (Some argIdent, arg)::ctx.scope }
                        ctx, assignments, (idx + 1)
                )
            let typeArgs = matchGenericParams ctx meth (typArgs, methTypArgs)
            let ctx = {ctx with typeArgs=typeArgs}
            let expr = com.Transform ctx fsExpr
            if List.isEmpty assignments
            then Some expr
            else makeSequential r (assignments@[expr]) |> Some

    let passGenerics com ctx r (typArgs, methTypArgs) meth =
        let rec hasUnresolvedGenerics = function
            | Fable.GenericParam name -> Some name
            | Fable.Option genericArg -> hasUnresolvedGenerics genericArg
            | Fable.Array genericArg -> hasUnresolvedGenerics genericArg
            | Fable.Tuple genericArgs -> genericArgs |> Seq.tryPick hasUnresolvedGenerics
            | Fable.Function (argTypes, returnType, _) -> returnType::argTypes |> Seq.tryPick hasUnresolvedGenerics
            | Fable.DeclaredType (_, genericArgs) -> genericArgs |> Seq.tryPick hasUnresolvedGenerics
            | _ -> None
        let genInfo = { makeGeneric=true; genericAvailability=ctx.genericAvailability }
        matchGenericParams ctx meth (typArgs, methTypArgs)
        |> List.map (fun (genName, FableType com ctx typ) ->
            if not ctx.genericAvailability then
                match hasUnresolvedGenerics typ with
                | Some name ->
                    ("An unresolved generic argument ('" + name + ") is being passed " +
                     "to a function with `PassGenericsAttribute`. This will likely fail " +
                     "at runtime. Try adding `PassGenericsAttribute` to the calling method " +
                     "or using concrete types.")
                    |> addWarning com ctx.fileName r
                | None -> ()
            genName, makeTypeRef com genInfo typ)
        |> makeJsObject None

    let (|ExtensionMember|_|) com (ctx: Context) r typ (callee, args, argTypes) owner (meth: FSharpMemberOrFunctionOrValue) =
        match meth.IsExtensionMember, callee, owner with
        | true, Some callee, Some ent ->
            let typRef = makeTypeFromDef com ctx.typeArgs ent [] |> makeNonGenTypeRef com
            let methName =
                let methName = sanitizeMethodName meth
                let ent = com.GetEntity ent
                let loc = if meth.IsInstanceMember then Fable.InstanceLoc else Fable.StaticLoc
                match ent.TryGetMember(methName, getMemberKind meth, loc, argTypes) with
                | Some m -> m.OverloadName | None -> methName
            let ext = makeGet r Fable.Any typRef (makeStrConst methName)
            // Bind the extension method so `this` has the proper value: extMethod.bind(callee)(...args)
            let bind =
                let meth = makeGet None Fable.Any ext (makeStrConst "bind")
                Fable.Apply(meth, [callee], Fable.ApplyMeth, Fable.Any, None)
            Fable.Apply (bind, args, Fable.ApplyMeth, typ, r) |> Some
        | _ -> None

    let getOverloadedName (com: IFableCompiler) owner meth kind methArgTypes methName =
        match owner with
        | Some ent ->
            let ent = com.GetEntity ent
            ent.TryGetMember(methName, kind, getMemberLoc meth, methArgTypes)
            |> function Some m -> m.OverloadName | None -> methName
        | None -> methName

    let makeCallFrom (com: IFableCompiler) ctx r typ
                     (meth: FSharpMemberOrFunctionOrValue)
                     (typArgs, methTypArgs) callee args =
        let methArgTypes = getArgTypes com meth.CurriedParameterGroups
        let args =
            let args = ensureArity com methArgTypes args
            if hasRestParams meth then
                let args = List.rev args
                match args.Head with
                | Fable.Value(Fable.ArrayConst(Fable.ArrayValues items, _)) ->
                    (List.rev args.Tail)@items
                | _ ->
                    (Fable.Spread args.Head |> Fable.Value)::args.Tail |> List.rev
            elif hasListParam meth then
                match List.splitAt (List.length args - 1) args with
                | rest, [last] ->
                    match last with
                    | CoreCons "List" "default" [] -> rest
                    | CoreMeth "List" "ofArray" [Fable.Value(Fable.ArrayConst(Fable.ArrayValues spreadValues, _))] -> rest @ spreadValues
                    | last -> rest @ [Fable.Value(Fable.Spread last)]
                | _ -> args
            else
                if hasAtt Atts.passGenerics meth.Attributes
                then args@[passGenerics com ctx r (typArgs, methTypArgs) meth]
                else removeOmittedOptionalArguments meth args // See #231, #640
        let owner = tryEnclosingEntity meth
        let i = buildApplyInfoFrom com ctx r typ (typArgs, methTypArgs, methArgTypes) (callee, args) owner meth
        match meth with
        // Check for replacements, emits...
        | EmitReplacement com i replaced -> replaced
        | Plugin com i replaced -> replaced
        | Imported com ctx r typ i (typArgs, methTypArgs) args imported -> imported
        | Emitted com ctx r typ i (typArgs, methTypArgs) (callee, args) emitted -> emitted
        | Erased com ctx r typ owner (callee, args) erased -> erased
        | Replaced com ctx owner i replaced -> replaced
        | Inlined com ctx r (typArgs, methTypArgs) (callee, args) expr -> expr
        | ExtensionMember com ctx r typ (callee, args, methArgTypes) owner expr -> expr
        | Try (tryGetBoundExpr ctx) e ->
            let args =
                match callee with
                | Some callee -> callee::args
                | None -> args
            Fable.Apply(e, args, Fable.ApplyMeth, typ, r)
        | _ ->
            // TODO: Check if this is an interface or overriden method
            // TODO: Check overloaded name
            failwith "TODO: Calls to method not bound in context"

    let makeValueFrom com ctx r typ eraseUnit (v: FSharpMemberOrFunctionOrValue) =
        let resolveValue com ctx r typ owner v =
            match tryGetBoundExpr ctx v with
            | Some e -> e
            | None ->
                let typ, typeRef =
                    match owner with
                    | Some ent -> typ, makeTypeFromDef com ctx.typeArgs ent [] |> makeNonGenTypeRef com
                    // Cases when tryEnclosingEntity returns None are rare, let's assume
                    // the value belongs to the current enclosing module and use
                    // type Any to avoid issues with `AST.Fable.Util.ensureArity`
                    // See MiscTests.``Recursive values work`` (#237)
                    | None ->
                        Fable.Any, Fable.DeclaredType(ctx.enclosingModule.Entity, []) |> makeNonGenTypeRef com
                Fable.Apply (typeRef, [makeStrConst v.CompiledName], Fable.ApplyGet, typ, r)
        if eraseUnit && typ = Fable.Unit
        then Fable.Wrapped(Fable.Value Fable.Null, Fable.Unit)
        elif v.IsModuleValueOrMember
        then
            let owner = tryEnclosingEntity v
            let i = buildApplyInfoFrom com ctx r typ ([], [], []) (None, []) owner v
            match v with
            | Plugin com i replaced -> replaced
            | Imported com ctx r typ i ([], []) [] imported -> imported
            | Emitted com ctx r typ i ([], []) (None, []) emitted -> emitted
            | Replaced com ctx owner i replaced -> replaced
            | v -> resolveValue com ctx r typ owner v
        else
            resolveValue com ctx r typ None v
