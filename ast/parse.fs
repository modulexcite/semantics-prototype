module WebAssembly.AST.Parse

open FParsec
open WebAssembly
open WebAssembly.SExpr
open WebAssembly.SExpr.Parse
open WebAssembly.AST.Module
open Microsoft.FSharp.Reflection
open System
open System.Reflection
open System.Collections.Generic

let _expression_lookup_table = 
  ref (null : Dictionary<string, Func<SExpr.Expression, AST.Expression>>)

let _statement_lookup_table =
  ref (null : Dictionary<string, Func<SExpr.Expression, AST.Statement>>)


// FIXME: gross
let duFromString (ty:Type) (s:string) =
    match FSharpType.GetUnionCases typeof<'a> |> Array.filter (fun case -> String.Equals(case.Name, s, StringComparison.InvariantCultureIgnoreCase)) with
    |[|case|] -> Some(FSharpValue.MakeUnion(case,[||]) :?> 'a)
    |_ -> None

let duFromKeyword (ty:Type) (kw:Value) =
  match kw with
  | Keyword s -> duFromString ty s
  | _         -> None

let astSymbolFromSExprSymbol s =
  match s with
  | SExpr.NamedSymbol     name -> AST.Symbol.NamedSymbol     name
  | SExpr.AnonymousSymbol idx  -> AST.Symbol.AnonymousSymbol idx


let rec _lookupTableCaseCtor<'T> untypedCaseCtor parseArguments caseName sExpr =
  let caseCtorArgs = parseArguments sExpr

  let result = (untypedCaseCtor(caseCtorArgs) : obj)
  result :?> 'T

and     _makeArgumentParser (ty:Type) =
  if ty = typeof<AST.Expression> then
    (fun (v : Value) ->
      match v with
      | Expression se -> 
        (
          match expressionFromSExpr se with
          | Some e -> box e
          | None   -> null
        )
    )
  elif ty = typeof<AST.Symbol> then
    (fun (v : Value) ->
      match v with
      | Symbol s -> box (astSymbolFromSExprSymbol s)
    )
  elif ty = typeof<AST.NumericLiteral> then
    (fun (v : Value) -> null)
  else
    (fun (v : Value) -> 
      match duFromKeyword ty v with
      | Some du -> du
    )

and     _makeLookupTableCaseCtor<'T> case =
  let untypedCaseCtor = FSharpValue.PreComputeUnionConstructor case
  let caseName = case.Name.ToLowerInvariant()
  let caseFields = case.GetFields()

  let argumentParsers =
    Array.map (fun (p : PropertyInfo) -> _makeArgumentParser p.PropertyType) caseFields

  let parseArguments = (fun (sExpr : SExpr.Expression) ->
    if not (sExpr.arguments.Length = caseFields.Length) then
      raise (
        new ArgumentException(
          String.Format(
            "{0} expects {1} argument(s), got {2}", caseName, caseFields.Length, sExpr.arguments.Length
          )
        )
      )

    let result = Array.zeroCreate caseFields.Length
    for i = 0 to caseFields.Length - 1 do
      let parsedValue = argumentParsers.[i] sExpr.arguments.[i];
      result.[i] <- parsedValue;

    result
  )

  let ctor = (_lookupTableCaseCtor<'T> untypedCaseCtor parseArguments caseName)
  (caseName, ctor)

and     _makeLookupTable<'T> () =
  let table = new Dictionary<string, Func<SExpr.Expression, 'T>>()
  let cases = FSharpType.GetUnionCases(typeof<'T>)

  for case in cases do
    let (caseName, ctor) = _makeLookupTableCaseCtor<'T> case
    table.Add(caseName, Func<SExpr.Expression, 'T>(ctor))

  table


and     getExpressionLookupTable () =
  if _expression_lookup_table.Value = null then
    (_expression_lookup_table := _makeLookupTable<AST.Expression> ())
    _expression_lookup_table.Value
  else
    _expression_lookup_table.Value

and     expressionFromSExpr sExpr =
  let name = sExpr.keyword.ToLowerInvariant()
  let table = getExpressionLookupTable ()
  let (found, ctor) = table.TryGetValue(name)

  if found then
    let result = ctor.Invoke(sExpr)
    Some result
  else
    printfn "No expression type named '%s'" name
    None    

and     getStatementLookupTable () =
  if _statement_lookup_table.Value = null then
    _statement_lookup_table := _makeLookupTable<AST.Statement> ()
    _statement_lookup_table.Value
  else
    _statement_lookup_table.Value

and     statementFromSExpr sExpr =
  let name = sExpr.keyword.ToLowerInvariant()
  let table = getStatementLookupTable ()
  let (found, ctor) = table.TryGetValue(name)

  if found then
    let result = ctor.Invoke(sExpr)
    Some result
  else
    let maybeExpression = expressionFromSExpr sExpr
    match maybeExpression with
    | Some e ->
      Some (AST.Statement.Expression e)
    | None ->
      printfn "No statement type named '%s'" name
      None


let read_block =
  readAbstractNamed "block" (
    (readMany read_sexpr) |>>
    (fun sExprs ->
      printfn "(block %A)" sExprs;
      ({
        Statements = List.choose statementFromSExpr sExprs
      } : Block)
    )
  )


let sectionName s =
  attempt (pstring s .>> spaces)

let read_symbolTable =
  sectionName "symbols" >>. spaces >>.
  (
    (readMany read_symbol) |>> SymbolTable
  )

let enumerant n v =
  (pstring n) >>. (preturn v)

let read_localType =
  (pstring ":") >>. choice [
    enumerant "int32"   LocalTypes.Int32;
    enumerant "int64"   LocalTypes.Int64;
    enumerant "float32" LocalTypes.Float32;
    enumerant "float64" LocalTypes.Float64;
  ] .>> spaces

let read_variable_declaration =
  pipe2 
    read_localType read_symbol 
    (fun t s -> { Name = s; Type = t })

let read_argument_types =
  readAbstractNamed "args" (
    spaces >>.
    readMany read_variable_declaration
  )

let read_local_variables =
  readAbstractNamed "locals" (
    spaces >>.
    readMany read_variable_declaration
  )

let read_declaration =
  readAbstractNamed "declaration" (
    (pipe3 
      read_symbol
      read_localType 
      read_argument_types
      (fun a b c ->
        {
          Name          = a;
          ReturnType    = b;
          ArgumentTypes = c;
        } : FunctionDeclaration
      )
    )
  )

let read_declarations =
  sectionName "declarations" >>.
    (readMany read_declaration) |>> FunctionDeclarations

let read_definition =
  readAbstractNamed "definition" (
    (pipe3 
      read_symbol
      read_local_variables
      read_block
      (fun a b c ->
        {
          Name           = a;
          LocalVariables = b;
          Body           = c;
        } : FunctionDefinition
      )
    )
  )

let read_definitions =
  sectionName "definitions" >>.
    (readMany read_definition) |>> FunctionDefinitions

let read_section =
  pstring "section:" >>. choice [
    read_symbolTable;
    read_declarations;
    read_definitions;
  ]

let read_topLevel = 
  spaces >>. (readManyAbstract read_section) |>> (fun sections -> { Sections = sections })

let topLevelFromString str =  
  run read_topLevel str