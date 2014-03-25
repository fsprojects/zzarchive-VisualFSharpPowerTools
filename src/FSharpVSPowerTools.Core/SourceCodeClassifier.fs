﻿module FSharpVSPowerTools.Core.SourceCodeClassifier

open System
open System.Collections.Generic
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices
open FSharpVSPowerTools
open FSharp.CompilerBinding

type Category =
    | ReferenceType
    | ValueType
    | PatternCase
    | TypeParameter
    | Function
    | PublicField
    | MutableVar
    | Quotation
    | Other
    override x.ToString() = sprintf "%A" x

let internal getCategory (symbolUse: FSharpSymbolUse) =
    let symbol = symbolUse.Symbol
    
    let isOperator (name: string) =
        if name.StartsWith "( " && name.EndsWith " )" && name.Length > 4 then
            name.Substring (2, name.Length - 4) |> String.forall (fun c -> c <> ' ')
        else false

    let rec getEntityAbbreviatedType (entity: FSharpEntity) =
        if entity.IsFSharpAbbreviation then
            let typ = entity.AbbreviatedType
            if typ.HasTypeDefinition then getEntityAbbreviatedType typ.TypeDefinition
            else entity
        else entity

    let rec getAbbreviatedType (fsharpType: FSharpType) =
        if fsharpType.IsAbbreviation then
            let typ = fsharpType.AbbreviatedType
            if typ.HasTypeDefinition then getAbbreviatedType typ
            else fsharpType
        else fsharpType

    let isReferenceCell (fsharpType: FSharpType) = 
        let ty = getAbbreviatedType fsharpType
        ty.HasTypeDefinition 
        && ty.TypeDefinition.IsFSharpRecord
        && ty.TypeDefinition.FullName = "Microsoft.FSharp.Core.FSharpRef`1"

    match symbol with
    | :? FSharpGenericParameter
    | :? FSharpStaticParameter -> 
        TypeParameter
    | :? FSharpUnionCase
    | :? FSharpActivePatternCase -> 
        PatternCase

    | :? FSharpField as f ->
        if f.IsMutable || isReferenceCell f.FieldType then MutableVar
        elif f.Accessibility.IsPublic then PublicField 
        else Other

    | :? FSharpEntity as e ->
        //debug "%A (type: %s)" e (e.GetType().Name)
        let e = getEntityAbbreviatedType e
        if e.IsEnum || e.IsValueType then ValueType
        elif e.IsClass || e.IsDelegate || e.IsFSharpExceptionDeclaration
           || e.IsFSharpRecord || e.IsFSharpUnion || e.IsInterface || e.IsMeasure || e.IsProvided
           || e.IsProvidedAndErased || e.IsProvidedAndGenerated 
           || (e.IsFSharp && e.IsOpaque && not e.IsFSharpModule && not e.IsNamespace) then
            ReferenceType
        else Other
    
    | :? FSharpMemberFunctionOrValue as func ->
        //debug "%A (type: %s)" mfov (mfov.GetType().Name)
        if func.CompiledName = ".ctor" then 
            if func.EnclosingEntity.IsValueType || func.EnclosingEntity.IsEnum then ValueType
            else ReferenceType
        elif func.FullType.IsFunctionType && not func.IsGetterMethod && not func.IsSetterMethod
             && not symbolUse.IsFromComputationExpression then 
            if isOperator func.DisplayName then Other
            else Function
        elif func.IsMutable || isReferenceCell func.FullType then MutableVar
        else Other
    
    | _ ->
        debug "Unknown symbol: %A (type: %s)" symbol (symbol.GetType().Name)
        Other

type CategorizedColumnSpan =
    { Category: Category
      WordSpan: WordSpan }

// If "what" span is entirely included in "from" span, then truncate "from" to the end of "what".
// Example: for ReferenceType symbol "System.Diagnostics.DebuggerDisplay" there are "System", "Diagnostics" and "DebuggerDisplay"
// plane symbols. After excluding "System", we get "Diagnostics.DebuggerDisplay",
// after excluding "Diagnostics", we get "DebuggerDisplay" and we are done.
let excludeWordSpan from what =
    if what.EndCol < from.EndCol && what.EndCol > from.StartCol then
        { from with StartCol = what.EndCol + 1 } // the dot between parts
    else from
 
let getCategoriesAndLocations (allSymbolsUses: FSharpSymbolUse[], untypedAst: ParsedInput option, lexer: ILexer) =
    let allSymbolsUses =
        allSymbolsUses
        // FCS can return multi-line ranges, let's ignore them
        |> Array.filter (fun symbolUse -> symbolUse.RangeAlternate.StartLine = symbolUse.RangeAlternate.EndLine)
      
    // index all symbol usages by LineNumber 
    let wordSpans = 
        allSymbolsUses
        |> Seq.map (fun su -> WordSpan.FromRange su.RangeAlternate)
        |> Seq.groupBy (fun r -> r.Line)
        |> Seq.map (fun (line, ranges) -> line, ranges)
        |> Map.ofSeq

    let spansBasedOnSymbolsUses = 
        allSymbolsUses
        |> Seq.choose (fun x ->
            let span = WordSpan.FromRange x.RangeAlternate
        
            let span = 
                match wordSpans.TryFind x.RangeAlternate.StartLine with
                | Some spans -> spans |> Seq.fold (fun result span -> excludeWordSpan result span) span
                | _ -> span

            let span' = 
                if (span.EndCol - span.StartCol) - x.Symbol.DisplayName.Length > 0 then
                    // The span is wider that the simbol's display name.
                    // This means that we have not managed to extract last part of a long ident accurately.
                    // Particulary, it happens for chained method calls like Guid.NewGuid().ToString("N").Substring(1).
                    // So we get ident from the lexer.
                    match lexer.GetSymbolAtLocation (x.RangeAlternate.Start.Line - 1) (span.EndCol - 1) with
                    | Some s -> 
                        match s.Kind with
                        | Ident -> 
                            // Lexer says that our span is too wide. Adjust it's left column.
                            if span.StartCol < s.LeftColumn then { span with StartCol = s.LeftColumn }
                            else span
                        | _ -> span
                    | _ -> span
                else span

            let categorizedSpan =
                if span'.EndCol <= span'.StartCol then None
                else Some { Category = getCategory x; WordSpan = span' }
        
            categorizedSpan)
        |> Seq.distinct
        |> Seq.toArray

    let quotationRanges = ref (ResizeArray<_>())

    let rec visitPattern = function
        | SynPat.Wild(_) -> ()
        | SynPat.Named(pat, _, _, _, _) ->
            visitPattern pat
        | SynPat.LongIdent(LongIdentWithDots(_, _), _, _, _, _, _) ->
            //let names = String.concat "." [ for i in ident -> i.idText ]
            //printfn "  .. identifier: %s" names
            ()
        | _ -> () // printfn "  .. other pattern: %A" pat

    let rec visitExpression = function
        | SynExpr.IfThenElse(cond, trueBranch, falseBranchOpt, _, _, _, _) ->
            // Visit all sub-expressions
            //printfn "Conditional:"
            visitExpression cond
            visitExpression trueBranch
            falseBranchOpt |> Option.iter visitExpression 
    
        | SynExpr.LetOrUse(_, _, bindings, body, _) ->
            // Visit bindings (there may be multiple 
            // for 'let .. = .. and .. = .. in ...'
            //printfn "LetOrUse with the following bindings:"
            for binding in bindings do
                let (Binding (_, _, _, _, _, _, _, pat, _, init, _, _)) = binding
                visitPattern pat 
                visitExpression init
            // Visit the body expression
            //printfn "And the following body:"
            visitExpression body
        | SynExpr.Quote (_, _isRaw, _quotedExpr, _, range) ->
            (!quotationRanges).Add range
        | SynExpr.App (_,_, funcExpr, argExpr, _) -> 
            visitExpression argExpr
            visitExpression funcExpr
        | x -> () // printfn " - not supported expression: %A" x

    let visitBinding (Binding(_, _, _, _, _, _, _, pat, _, body, _, _)) =
        visitPattern pat 
        visitExpression body         

    let visitMember = function
        | SynMemberDefn.LetBindings (bindings, _, _, _) -> for b in bindings do visitBinding b
        | SynMemberDefn.Member (binding, _) -> visitBinding binding
        | _ -> () //printfn "Unknown type member: %A" x

    let visitType ty =
        let (SynTypeDefn.TypeDefn (_, repr, _, _)) = ty
        match repr with
        | SynTypeDefnRepr.ObjectModel (_, defns, _) ->
            for d in defns do visitMember d
        | _ -> ()

    let visitDeclarations decls = 
        for declaration in decls do
            match declaration with
            | SynModuleDecl.Let (_, bindings, _) -> for b in bindings do visitBinding b
            | SynModuleDecl.Types (types, _) -> for ty in types do visitType ty
            | _ -> () // printfn " - not supported declaration: %A" declaration

    let visitModulesAndNamespaces modulesOrNss =
        for moduleOrNs in modulesOrNss do
            let (SynModuleOrNamespace(_, _, decls, _, _, _, _)) = moduleOrNs
            visitDeclarations decls

    untypedAst |> Option.iter (fun ast ->
        match ast with
        | ParsedInput.ImplFile(implFile) ->
            // Extract declarations and walk over them
            let (ParsedImplFileInput(_, _, _, _, _, modules, _)) = implFile
            visitModulesAndNamespaces modules
        | _ -> ()
    )

    //printfn "AST: %A" untypedAst
    
    let quotations =
        !quotationRanges 
        |> Seq.map (fun (r: Range.range) -> 
            if r.EndLine = r.StartLine then
                seq [ { Category = Quotation
                        WordSpan = { Line = r.StartLine
                                     StartCol = r.StartColumn
                                     EndCol = r.EndColumn }} ]
            else
                [r.StartLine..r.EndLine]
                |> Seq.map (fun line ->
                     let tokens = lexer.GetAllTokens (line - 1)

                     let tokens =
                        match tokens |> List.tryFind (fun t -> t.TokenName = "LQUOTE") with
                        | Some lquote -> tokens |> Seq.skipWhile (fun t -> t <> lquote) |> Seq.toList
                        | _ ->
                            match tokens |> List.tryFind (fun t -> t.TokenName = "RQUOTE") with
                            | Some rquote -> 
                                tokens 
                                |> List.rev
                                |> Seq.skipWhile (fun t -> t <> rquote)
                                |> Seq.toList
                                |> List.rev
                                |> Seq.skipWhile (fun t -> t.CharClass = TokenCharKind.WhiteSpace)
                                |> Seq.toList
                            | _ ->
                                tokens
                                |> Seq.skipWhile (fun t -> t.CharClass = TokenCharKind.WhiteSpace)
                                |> Seq.toList

                     let minCol = tokens |> List.map (fun t -> t.LeftColumn) |> function [] -> 0 | xs -> xs |> List.min
                 
                     let maxCol = 
                        match tokens with
                        | [] -> 0
                        | xs ->
                            let tok = xs |> List.maxBy (fun t -> t.RightColumn) 
                            tok.LeftColumn + tok.FullMatchedLength

                     { Category = Quotation
                       WordSpan = { Line = line
                                    StartCol = minCol
                                    EndCol = maxCol }}))
        |> Seq.concat
        |> Seq.toArray

    let allSpans = spansBasedOnSymbolsUses |> Array.append quotations
    
    //for span in allSpans do
       //debug "-=O=- %A" span

    allSpans
