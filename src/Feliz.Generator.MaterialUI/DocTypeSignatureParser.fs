﻿module DocTypeSignatureParser

open System
open FParsec
open Feliz.Generator
open Domain

module Parsers =

    type Parser<'t> = Parser<'t, unit>

    let tsIdentifier<'State> : Parser<string, 'State> = identifier (IdentifierOptions())

    let ws = unicodeSpaces

    let tsStringLiteral (stringLiteralParser: Parser<string, 'State>) =
        let tsStringSingleQuote = pchar '''
        let tsSTringDoubleQuote = pchar '"'
        between tsStringSingleQuote tsStringSingleQuote stringLiteralParser
        <|> between tsSTringDoubleQuote tsSTringDoubleQuote stringLiteralParser

    let tsAtomicType: Parser<TsType> =
        choice [
            stringReturn "string" String
            stringReturn "number" Number
            stringReturn "bool" Bool
            stringReturn "func" Func
            stringReturn "object" TsAtomicType.Object
            stringReturn "elementType" ElementType
            stringReturn "element" Element
            tsStringLiteral tsIdentifier |>> StringLiteral
            tsIdentifier |>> OtherType
        ] |>> TsType.Atomic

    let tsType, tsTypeRef = createParserForwardedToRef<TsType, unit>()

    let tsObject: Parser<TsType> =
    
        let objectEntry =
            let objField =
                tsIdentifier .>>. (opt (pchar '?'))
                |>> (fun (fieldName, isOptionalMarker) -> fieldName, isOptionalMarker.IsSome)
        
            tuple2
                objField
                (ws >>. pchar ':' .>> ws >>. tsType)
            |>> (fun ((fieldName, isOptional), fieldType) -> fieldName, fieldType, isOptional)
    
        let objectEntries = sepEndBy1 objectEntry (pchar ',' .>> ws)
    
        between (pchar '{') (pchar '}') (ws >>. objectEntries .>> ws)
        |>> TsType.Object

    let tsArray =
        pstring "Array" >>. (between (pchar '<') (pchar '>') (ws >>. tsType .>> ws))
        |>> TsType.Array

    let tsNonUnionType =
        choice [ tsObject; tsArray; tsAtomicType]

    let tsPlainUnionCases (pElement: Parser<'t, _>) =
        ws >>. (sepBy1 (pElement .>> ws) (pchar '|' .>> ws))

    let tsUnion: Parser<TsType> =
        tsPlainUnionCases tsNonUnionType
        |>> (function
                | [t] -> t
                | ts -> Union ts)

    do tsTypeRef.Value <-
            choice [
                tsObject
                tsUnion
                tsArray
                tsAtomicType
            ]

    let tsTypeSignature =
        ws >>. tsType .>> ws .>> eof


module Translators =

    type FsTypeSignature = string

    type Translators = {
        Atomic: TsAtomicType -> FsTypeSignature
        InnerObject: (string * TsType * bool) list -> FsTypeSignature
        InnerUnion: TsType list -> FsTypeSignature
        InnerArray: TsType -> FsTypeSignature
        TopLevelObject: (string * TsType * bool) list -> PropOverload list
        TopLevelUnion: TsType list -> PropOverload list
        TopLevelArray: TsType -> PropOverload list
    }

    let translateTsAtomicType (tsAtomicType: TsAtomicType) =
        match tsAtomicType with
        | String -> "string"
        | Number -> "float"
        | Bool -> "bool"
        | Func -> "Func<obj, obj>"
        | TsAtomicType.Object -> "obj"
        | Element -> "ReactElement"
        | ElementType -> "ReactElementType"
        | StringLiteral s -> "\"" + s + "\""
        | OtherType typeName -> typeName


    let rec translateNestedTsTypeSign customize (tsType: TsType) =
        let translators = translators customize
        match tsType with
        | Atomic t -> translators.Atomic t
        | Array elType -> translators.InnerArray elType
        | Union cases -> translators.InnerUnion cases
        | TsType.Object objEntries -> translators.InnerObject objEntries

    and translateNestedArrayTypeSign customize (elementType: TsType) =
        translateNestedTsTypeSign customize elementType + " []"

    and translateNestedUnionTypeSign customize (unionCases: TsType list) =
        let translatedUnionCases =
            unionCases
            |> List.map (function
                | TsType.Atomic (TsAtomicType.StringLiteral _) -> "string"
                | t -> t |> translateNestedTsTypeSign customize)
            |> List.distinct
        let unionArity = translatedUnionCases.Length

        match translatedUnionCases with
        | [] ->
            invalidArg (nameof unionArity) ("Union cannot be empty")

        | [t] -> t

        | _ when unionArity > 9 ->
            invalidArg (nameof unionArity) ("Unions with more than 9 cases are not supported")

        | cases ->
            let unionTypeParamsList = cases |> String.concat ", "
            sprintf "U%i<%s>" unionArity unionTypeParamsList

    and translateNestedObjectTypeSign customize (objEntries: (string * TsType * bool) list) =
        let entries =
            objEntries
            |> List.map (fun (fieldName, fieldType, isOptional) ->
                let fsFieldName = jsParamNameToFsParamName fieldName
                let fsTypeSign =
                    let rawTypeSign = translateNestedTsTypeSign customize fieldType
                    if isOptional then rawTypeSign + " option"
                    else rawTypeSign
                fsFieldName + ": " + fsTypeSign)
            |> String.concat "; "
        "{| " + entries + " |}"

    and translateTopLevelTsType (customize: Translators -> Translators) (tsType: TsType) =
        let translators = translators customize
        match tsType with
        | Atomic t ->
            let typeSign = translators.Atomic t
            RegularPropOverload.create ("(value: " + typeSign + ")") "value"
            |> PropOverload.Regular
            |> List.singleton

        | Union cases -> translators.TopLevelUnion cases
        | Object objEntries -> translators.TopLevelObject objEntries
        | Array elType -> translators.TopLevelArray elType

    and translateTopLevelObject customize (objEntries: (string * TsType * bool) list) =
        let translatedParams =
            objEntries
            |> List.map (fun (fieldName, fieldType, isOptional) ->
                fieldName, translateNestedTsTypeSign customize fieldType, isOptional)

        let paramsListStr =
            translatedParams
            |> List.map (fun (fieldName, fieldType, isOptional) ->
                if isOptional then "?" else ""
                + jsParamNameToFsParamName fieldName
                + ": "
                + fieldType)
            |> String.concat ", "

        let objCreationCode =
            translatedParams |> jsObjectFromParamsCode jsParamNameToFsParamName

        RegularPropOverload.create
            ("(" + paramsListStr + ")")
            objCreationCode
        |> PropOverload.Regular
        |> List.singleton

    and translateTopLevelUnion customize (unionCases: TsType list) =
        unionCases
        |> List.collect (function
            | TsType.Atomic (TsAtomicType.StringLiteral s) ->
                EnumPropOverload.create (jsParamNameToFsParamName s) ("\"" + s + "\"")
                |> PropOverload.Enum
                |> List.singleton
            | t -> translateTopLevelTsType customize t)
        |> List.distinctBy (function
            | PropOverload.Regular p -> Choice1Of2 p.ParamsCode
            | PropOverload.Enum p -> Choice2Of2 (p.MethodName, p.ParamsCode))

    and translateTopLevelArray customize (elementType: TsType) =
        let translatedTypeSign = translateNestedTsTypeSign customize elementType
        let methodParamsCode =
            sprintf "([<ParamArray>] values: %s [])" translatedTypeSign
        let methodBodyCode = "values"

        RegularPropOverload.create methodParamsCode methodBodyCode
        |> PropOverload.Regular
        |> List.singleton

    and translators (customize: Translators -> Translators): Translators =
        {
            Atomic = translateTsAtomicType
            InnerObject = translateNestedObjectTypeSign customize
            InnerUnion = translateNestedUnionTypeSign customize
            InnerArray = translateNestedArrayTypeSign customize
            TopLevelObject = translateTopLevelObject customize
            TopLevelUnion = translateTopLevelUnion customize
            TopLevelArray = translateTopLevelArray customize
        } |> customize

    type Translators with
        static member Default = translators id

type Translators = Translators.Translators

let tryParseTypeSignatureString (str: string) =
    str
    |> run Parsers.tsTypeSignature
    |> function
        | Success(t, _, _) -> Result.Ok t
        | Failure(errorMsg, _, _) -> Result.Error errorMsg

let translateDefault (typeSignature: TsType) =
    typeSignature
    |> Translators.translateTopLevelTsType id

let translateCustom (customizeTranslators: Translators -> Translators) typeSignature =
    typeSignature
    |> Translators.translateTopLevelTsType customizeTranslators

let parseAndTranslateDefault (typeSignatureString: string) =
    typeSignatureString
    |> tryParseTypeSignatureString
    |> Result.map translateDefault

let parseAndTranslateCustom (customizeTranslators: Translators -> Translators) (typeSignatureString: string) =
    typeSignatureString
    |> tryParseTypeSignatureString
    |> Result.map (translateCustom customizeTranslators)