﻿namespace FSharpVSPowerTools.ProjectSystem

open System
open System.Reflection
open Microsoft.FSharp.Reflection

module Reflection = 
    // Various flags configurations for Reflection
    let staticFlags = BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Static
    let instanceFlags = BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Instance
    let ctorFlags = instanceFlags
    let inline asMethodBase (a : #MethodBase) = a :> MethodBase
    
    let (?) (o : obj) name : 'R = 
        // The return type is a function, which means that we want to invoke a method
        if FSharpType.IsFunction(typeof<'R>) then 
            let argType, _resType = FSharpType.GetFunctionElements(typeof<'R>)            
            FSharpValue.MakeFunction(typeof<'R>, 
                fun args -> 
                    // We treat elements of a tuple passed as argument as a list of arguments
                    // When the 'o' object is 'System.Type', we call static methods
                    let methods, instance, args = 
                        let typeInfo = o.GetType()
                        let args = 
                            if argType = typeof<unit> then [||]
                            elif not (FSharpType.IsTuple(argType)) then [| args |]
                            else FSharpValue.GetTupleFields(args)                        
                        if (typeof<System.Type>).IsAssignableFrom(typeInfo) then 
                            let methods = (unbox<Type> o).GetMethods(staticFlags) |> Array.map asMethodBase
                            let ctors = 
                                (unbox<Type> o).GetConstructors(ctorFlags) 
                                |> Array.map asMethodBase
                            Array.concat [ methods; ctors ], null, args
                        else 
                            typeInfo.GetMethods(instanceFlags) |> Array.map asMethodBase, o, 
                            args
                                         
                    // A simple overload resolution based on the name and number of parameters only
                    let methods = 
                        [ for m in methods do
                            if m.Name = name && m.GetParameters().Length = args.Length then 
                                yield m ]
                                         
                    match methods with
                    | [] -> failwithf "No method '%s' with %d arguments found" name args.Length
                    | _ :: _ :: _ -> 
                        failwithf "Multiple methods '%s' with %d arguments found" name args.Length
                    | [ :? ConstructorInfo as c ] -> c.Invoke(args)
                    | [ m ] -> m.Invoke(instance, args))
            |> unbox<'R>
        else 
            // When the 'o' object is 'System.Type', we access static properties
            let typ, flags, instance = 
                if (typeof<System.Type>).IsAssignableFrom(o.GetType()) then unbox o, staticFlags, null
                else o.GetType(), instanceFlags, o
            
            // Find a property that we can call and get the value
            let prop = typ.GetProperty(name, flags)
            if prop = null then failwithf "Property '%s' not found in '%s' using flags '%A'." name typ.Name flags
            let meth = prop.GetGetMethod(true)
            if prop = null then failwithf "Property '%s' found, but doesn't have 'get' method." name
            meth.Invoke(instance, [||]) |> unbox<'R>

open Reflection
open Microsoft.VisualStudio.TextManager.Interop

exception AssemblyMissingException of string

type internal FSharpLanguageService private () =      
    static let asm = 
      lazy try Assembly.Load("FSharp.LanguageService, Version=12.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")
           with _ -> raise (AssemblyMissingException "FSharp.LanguageService")
    static member VsTextColorState = asm.Value.GetType("Microsoft.VisualStudio.FSharp.LanguageService.VsTextColorState")
    static member ColorStateLookup = asm.Value.GetType("Microsoft.VisualStudio.FSharp.LanguageService.ColorStateLookup")

type internal VsTextColorState =
    static member GetColorStateAtStartOfLine(vsColorState: IVsTextColorState, line: int): int =
        FSharpLanguageService.VsTextColorState?GetColorStateAtStartOfLine(vsColorState, line)

type internal ColorStateLookup =
    static member LexStateOfColorState(colorState: int): int64 =
        FSharpLanguageService.ColorStateLookup?LexStateOfColorState(colorState)
        
