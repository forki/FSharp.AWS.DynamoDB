﻿namespace FSharp.AWS.DynamoDB

open System
open System.Collections
open System.Collections.Generic
open System.Collections.Concurrent
open System.IO
open System.Reflection

open Microsoft.FSharp.Reflection

open FSharp.AWS.DynamoDB
open FSharp.AWS.DynamoDB.TypeShape

//
//  Pickler resolution implementation
//

[<AutoOpen>]
module private ResolverImpl =

    let resolvePickler (resolver : IPicklerResolver) (t : Type) : Pickler =
        match getShape t with
        | :? ShapeBool -> new BoolPickler() :> _
        | :? ShapeByte -> mkNumericalPickler<byte> () :> _
        | :? ShapeSByte -> mkNumericalPickler<sbyte> () :> _
        | :? ShapeInt16 -> mkNumericalPickler<int16> () :> _
        | :? ShapeInt32 -> mkNumericalPickler<int32> () :> _
        | :? ShapeInt64 -> mkNumericalPickler<int64> () :> _
        | :? ShapeUInt16 -> mkNumericalPickler<uint16> () :> _
        | :? ShapeUInt32 -> mkNumericalPickler<uint32> () :> _
        | :? ShapeUInt64 -> mkNumericalPickler<uint64> () :> _
        | :? ShapeSingle -> mkNumericalPickler<single> () :> _
        | :? ShapeDouble -> mkNumericalPickler<double> () :> _
        | :? ShapeDecimal -> mkNumericalPickler<decimal> () :> _
        | :? ShapeChar -> new CharPickler() :> _
        | :? ShapeString -> new StringPickler() :> _
        | :? ShapeGuid -> new GuidPickler() :> _
        | :? ShapeByteArray -> new ByteArrayPickler() :> _
        | :? ShapeTimeSpan -> new TimeSpanPickler() :> _
        | :? ShapeDateTime -> UnSupportedType.Raise(t, "please use DateTimeOffset instead.")
        | :? ShapeDateTimeOffset -> new DateTimeOffsetPickler() :> _
        | _ when t = typeof<System.IO.MemoryStream> -> new MemoryStreamPickler() :> _
        | ShapeEnum s ->
            s.Accept {
                new IEnumVisitor<Pickler> with
                    member __.VisitEnum<'E, 'U when 'E : enum<'U>> () =
                        new EnumerationPickler<'E, 'U>() :> _ }

        | ShapeNullable s ->
            s.Accept {
                new INullableVisitor<Pickler> with
                    member __.VisitNullable<'T when 'T : (new : unit -> 'T) and 'T :> ValueType and 'T : struct> () = 
                        new NullablePickler<'T>(resolver.Resolve()) :> _ }

        | ShapeFSharpOption s ->
            s.Accept {
                new IFSharpOptionVisitor<Pickler> with
                    member __.VisitFSharpOption<'T> () =
                        let tp = resolver.Resolve<'T>()
                        new OptionPickler<'T>(tp) :> _ }

        | ShapeArray s ->
            s.Accept {
                new IArrayVisitor<Pickler> with
                    member __.VisitArray<'T> () =
                        let tp = resolver.Resolve<'T>()
                        new ListPickler<'T [], 'T>(Seq.toArray, null, tp) :> _ }

        | ShapeFSharpList s ->
            s.Accept {
                new IFSharpListVisitor<Pickler> with
                    member __.VisitFSharpList<'T> () =
                        let tp = resolver.Resolve<'T>()
                        new ListPickler<'T list, 'T>(List.ofSeq, [], tp) :> _ }

        | ShapeResizeArray s ->
            s.Accept {
                new IResizeArrayVisitor<Pickler> with
                    member __.VisitResizeArray<'T> () =
                        let tp = resolver.Resolve<'T>()
                        new ListPickler<ResizeArray<'T>, 'T>(rlist, null, tp) :> _ }

        | ShapeFSharpSet s ->
            s.Accept {
                new IFSharpSetVisitor<Pickler> with
                    member __.VisitFSharpSet<'T when 'T : comparison> () =
                        mkSetPickler<'T>(resolver.Resolve()) :> _ }

        | ShapeFSharpMap s ->
            s.Accept { 
                new IFSharpMapVisitor<Pickler> with
                    member __.VisitFSharpMap<'K, 'V when 'K : comparison> () =
                        if typeof<'K> <> typeof<string> then
                            UnSupportedType.Raise(t, "Map types must have key of type string.")

                        new MapPickler<'V>(resolver.Resolve()) :> _ }

        | ShapeCollection s ->
            s.Accept {
                new ICollectionVisitor<Pickler> with
                    member __.VisitCollection<'T> () =
                        let tp = resolver.Resolve<'T>()
                        new ListPickler<ICollection<'T>, 'T>(Seq.toArray >> unbox, null, tp) :> _ }

        | ShapeEnumerable s ->
            s.Accept {
                new IEnumerableVisitor<Pickler> with
                    member __.VisitEnumerable<'T> () =
                        let tp = resolver.Resolve<'T>()
                        new ListPickler<seq<'T>, 'T>(Seq.toArray >> unbox, null, tp) :> _ }

        | ShapeTuple as s ->
            s.Accept {
                new IFunc<Pickler> with
                    member __.Invoke<'T> () = mkTuplePickler<'T> resolver :> _ }

        | s when FSharpType.IsRecord(t, true) ->
            s.Accept {
                new IFunc<Pickler> with
                    member __.Invoke<'T>() = mkFSharpRecordPickler<'T> resolver :> _   }

        | s when FSharpType.IsUnion(t, true) ->
            s.Accept {
                new IFunc<Pickler> with
                    member __.Invoke<'T>() = new UnionPickler<'T>(resolver) :> _   }

        | _ -> UnSupportedType.Raise t

    type CachedResolver private () as self =
        static let globalCache = new ConcurrentDictionary<Type, Lazy<Pickler>>()
        let stack = new Stack<Type>()
        let resolve t = 
            if stack.Contains t then
                UnSupportedType.Raise(t, "recursive types not supported.")
                
            stack.Push t
            let pf = globalCache.GetOrAdd(t, fun t -> lazy(resolvePickler self t))
            let _ = stack.Pop()
            pf.Value

        interface IPicklerResolver with
            member __.Resolve(t : Type) = resolve t
            member __.Resolve<'T> () = resolve typeof<'T> :?> Pickler<'T>

        static member Resolve(t : Type) =
            let ok, found = globalCache.TryGetValue t
            if ok then found.Value
            else
                (new CachedResolver() :> IPicklerResolver).Resolve t

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal Pickler =

    /// Resolves pickler for given type
    let resolveUntyped (t : Type) = CachedResolver.Resolve t
    /// Resolves pickler for given type
    let resolve<'T> () = CachedResolver.Resolve typeof<'T> :?> Pickler<'T>