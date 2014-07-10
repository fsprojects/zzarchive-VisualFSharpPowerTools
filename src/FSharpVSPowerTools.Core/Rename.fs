﻿module FSharpVSPowerTools.Rename.Checks

open FSharpVSPowerTools
open Microsoft.FSharp.Compiler.PrettyNaming

let DoubleBackTickDelimiter = "``"

let isDoubleBacktickIdent (s: string) =
    let doubledDelimiter = 2 * DoubleBackTickDelimiter.Length
    if s.StartsWith(DoubleBackTickDelimiter) && s.EndsWith(DoubleBackTickDelimiter) && s.Length > doubledDelimiter then
        let inner = s.Substring(DoubleBackTickDelimiter.Length, s.Length - doubledDelimiter)
        not (inner.Contains(DoubleBackTickDelimiter))
    else false

let isIdentifier (s: string) =
    if isDoubleBacktickIdent s then
        true
    else
        s |> Seq.mapi (fun i c -> i, c)
          |> Seq.forall (fun (i, c) -> 
                if i = 0 then IsIdentifierFirstCharacter c else IsIdentifierPartCharacter c) 

/// Encapsulates identifiers for rename operations if needed
let encapsulateIdentifier symbolKind newName =
    let isKeyWord = List.exists ((=) newName) Microsoft.FSharp.Compiler.Lexhelp.Keywords.keywordNames    
    let isAlreadyEncapsulated = newName.StartsWith DoubleBackTickDelimiter && newName.EndsWith DoubleBackTickDelimiter

    if isAlreadyEncapsulated then newName
    elif symbolKind = SymbolKind.Operator then newName
    elif isKeyWord || not (isIdentifier newName) then DoubleBackTickDelimiter + newName + DoubleBackTickDelimiter
    else newName