namespace XTargets

open Elmish

module Elmish =


    /// A function that transforms one type into another type
    type Transform<'a,'b>='a->'b
    /// A function that maps a value to a value of the same type
    type Update<'a> = Transform<'a,'a>
    /// A function that maps a value to a value of the same type
    type PartialUpdate<'a,'b>='a->'b->'b

    /// A function that generates values when called
    type Generator<'a> = unit->'a

    /// A function that transforms one type into another type
    type TransformAsync<'a,'b> = 'a->Async<'b>
    /// A function that asynchronously maps a value to a value of the same type
    type UpdateAsync<'a> = TransformAsync<'a,'a>
    /// A function that asynchronously maps a value to a value of the same type
    type PartialUpdateAsync<'a,'b> = 'a->'b->Async<'b>

    /// Access to a subset of the data in state
    type Lens<'a,'b> = Transform<'a,'b>*PartialUpdate<'b,'a>
    /// A lens that has async update behaviour. 
    type LensAsync<'a,'b> = Transform<'a,'b>*PartialUpdateAsync<'b,'a>

    /// A two way transformation 
    type Isomorphism<'a,'b> = Transform<'a,'b>*Transform<'b,'a>
    /// An async two way transformation
    type IsomorphismAsync<'a,'b> = Transform<'a,'b>*TransformAsync<'b,'a>

    /// Like a morpher but might fail when converting 'Val to 'State
    type Epimorphism<'a,'b,'error> = Transform<'a,'b>*Transform<'b,Core.Result<'a,'error>>
    /// An async two way transformation that might fail
    type EpimorphismAsync<'a,'b,'error> = Transform<'a,'b>*Transform<'b,Core.Result<'a,'error> Async>

    /// A message to with instruction  to
    type Message<'State> = Update<'State>

    module Update =
        let ofUpdate (u: Update<'State>): Message<'State> =
            u

        let ofValue (value: 'State): Message<'State> = 
            (fun _ -> value) |> ofUpdate

        let ofNone =
            id

    /// A type that holds a value and a dispatcher
    /// for updating that value. Why is it called an 
    /// 'Image'? When you focus a lens on an object
    /// you get an image. Probably a terrible analogy.
    type Image<'State when 'State : equality>(value:unit->'State, dispatch:Message<'State>->unit) =

        let value = value
        let dispatch = dispatch

        member x.Get with get() = value()
        member x.Set(newValue: 'State) =
            if newValue <> value() then
                newValue
                |> Update.ofValue
                |> dispatch
                ()

        member x.Update(update:'State->'State) =
            Update.ofUpdate update |> dispatch

        member x.Update(update:'State->'State Async) =
            let update' v =
                async {
                    let context = System.Threading.SynchronizationContext.Current
                    let! r = update v
                    do! Async.SwitchToContext context
                    Update.ofValue r |> dispatch
                } |> Async.StartImmediate
                v
            Update.ofUpdate update' |> dispatch

        /// Generate new lens for child data of the parent lens
        member x.Focus ((getter,setter): Lens<'State,'Val> ) : Image<'Val>  =

            let value() = getter x.Get

            let messageUpdater (update:Message<'Val>) = 
                    (
                        let update' (state:'State) =
                            let v = getter state 
                            let v' = update v 
                            let state' = setter v' state 
                            state'
                        Update.ofUpdate update'
                    )

            let dispatch' msg = messageUpdater msg |> dispatch

            Image(value, dispatch')

        member x.FocusAsync ((getter,setter):LensAsync<'State,'Val>   ) : Image<'Val>  =
            let value() = getter x.Get

            // for example 'Val is string
            let messageUpdater (update:Message<'Val>) = 
                    (
                        let update' = fun (state:'State) -> 
                            let v = getter state 
                            let v' = update v 
                            async {
                                let context = System.Threading.SynchronizationContext.Current
                                let! state' = setter v' state 
                                do! Async.SwitchToContext context
                                Update.ofValue state' |> dispatch
                            } |> Async.StartImmediate
                            state
                        Update.ofUpdate update'
                    )

            let dispatch' msg = messageUpdater msg |> dispatch

            Image(value,dispatch')

        /// Generate a new lens for a two way transformation of the parent lens.
        /// Note that it might not be possible to convert back from the value.
        /// In this case the old value will be used
        member x.Morph((getter,setter):Isomorphism<'State,'Val>) : Image<'Val> =
            let setter' v _ = setter v 
            x.Focus(getter,setter')

        member x.MorphAsync((getter,setter):IsomorphismAsync<'State,'Val>) : Image<'Val> =
            let setter' v _ = setter v 
            x.FocusAsync(getter,setter')

        member x.ToOption (defaultValue:'State) : Image<'State option> =
            let getter' state  = Some state
            let setter' (v:'State option) (s:'State) : 'State =
                match v with 
                | Some v' -> v'
                | None -> defaultValue
            x.Focus(getter',setter')

        /// Generate a new lens for a two way transformation of the parent lens.
        /// Note that it might not be possible to convert back from the value.
        /// In this case the old value will be used
        member x.Parse (error:Image<'Error option> ) ((getter:Transform<'State,'Val>,setter):Epimorphism<'State,'Val,'Error>) : Image<'Val> =
            let getter' = getter
            let setter' (v:'Val) (s:'State) =
                match setter v with 
                | Ok o -> 
                    None |> error.Set 
                    o
                | Error err -> 
                    Some err |> error.Set
                    s
            x.Focus(getter',setter')


        /// Generate a new lens for a two way transformation of the parent lens.
        /// Note that it might not be possible to convert back from the value.
        /// In this case the old value will be used
        member x.ParseAsync (error:Image<'Error>) ((getter,setter):EpimorphismAsync<'State,'Val,'Error>) : Image<'Val> =

            let setter' v state = 
                async {
                    let context = System.Threading.SynchronizationContext.Current
                    let! v' = setter v
                    do! Async.SwitchToContext context
                    match v' with 
                    | Ok state' -> 
                        return state'
                    | Error e -> 
                        error.Set(e)
                        return state
                }
            x.FocusAsync(getter,setter')

    module Lens =

        module Error =
            let inline toOption (a:Image<'a*bool> ) : Image<'a option> =
                let getter() = a.Get |> fst |> Some
                let dispatch' updater =
                    let av = getter() |> updater
                    match av with 
                    | Some k -> a.Set(k,true) // dispatch the new value and a flag that there is no error
                    | None -> a.Set(a.Get|>fst,false) // dispatch the old value and a flag that this is an error
                Image(getter, dispatch')

        module Tuple = 
            let inline mk2 (a:Image<'a> ) (b:Image<'b>) : Image<'a*'b> =
                let getter() = (a.Get,b.Get)
                let dispatch' updater =
                    let (av,bv)= getter() |> updater
                    a.Set av
                    b.Set bv
                Image(getter, dispatch')


        module Array =
            /// Generate a lens for an array based on matching the array item by some condition
            let find (pred:'a->bool) = 
                let setter (c:'a) (s:'a array) = 
                        s 
                        |> Seq.map ( fun c' -> if pred(c') then c else c') 
                        |> Seq.toArray
                let getter (s:'a array) =
                    s
                    |> Seq.find ( pred )
                getter,setter


            /// Build a lens to the specific item in the array
            let at (index:int)  =
                let setter (c:'a) (s:'a array) = 
                        s 
                        |> Seq.indexed
                        |> Seq.map ( fun (id, c') -> if id = index then c else c') 
                        |> Seq.toArray
                let getter (s:'a array) =
                    s.[index]

                getter,setter


            /// <![CDATA[[Map a Lens<'a array> to Lens<'a> array]]>
            /// <param name="compare">When replacing one item with another this returns true if the item should be replaced with the changed item</param>
            /// <param name="lens">The lens to the original array</param>
            let each (lens:Image<'State array>) =
                lens.Get
                    |> Seq.indexed
                    |> Seq.map ( fun (id,_) ->  lens.Focus(at id))
                    |> Seq.toArray


       type Image<'State when 'State:equality> with
            static member (>->) ((root:Image<'State>), (child:Lens<'State,'Val>)) =
                root.Focus child


    module Program =
        /// Make a program with some an initial state and a view factory that will accept
        /// an Image to the initial state. 
        let mkLensProgram (state:'State) (view:Image<'State>->'IView) =
            // Process commands at the top level so discard them
            // to meet the type signiture of mkProgram
            let update update state = 
                update state,Cmd.none

            // Wrap the mainview view function and inject the bin file reviewer action
            let view state dispatch = 
                let getter = fun() -> state
                view (Image(getter,dispatch))

            Elmish.Program.mkProgram (fun () -> state,Cmd.none) update view