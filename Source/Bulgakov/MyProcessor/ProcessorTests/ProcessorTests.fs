﻿module NUnitTests

open TTA.ASM
open NUnit.Framework
open Processor
  
[<TestFixture>]
type CreateProcessor() =
            
   [<Test>]
    member this.``Create abstract processor``() =        
        let processor = new Processor<_>([|(fun x y -> x); (fun x y-> y);|])
        Assert.AreEqual(0, processor.ValueAt 186 0)
    [<Test>]
    member this.``Create integer processor``() =        
        let processor = new Processor<int>([|(fun x y -> x+1); (fun x y-> x*x);|])
        Assert.AreEqual(0, processor.ValueAt 186 0)
    [<Test>]
    member this.``Create boolean processor``() =        
        let processor = new Processor<_>([|(fun x y -> x||y); (fun x y-> x&&y);|])
        Assert.AreEqual(false, processor.ValueAt 186 0)
    [<Test>]
    member this.``Create byte processor``() =        
        let processor = new Processor<byte>([|(fun x y -> x+y); (fun x y-> x*y);|])
        Assert.AreEqual(0uy, processor.ValueAt 186 0)
    [<Test>]
    member this.``Create array<string> processor``() =        
        let processor = new Processor<array<string>>([|(fun x y -> x); (fun x y-> y);|])
        Assert.AreEqual(null, processor.ValueAt 186 0)

[<TestFixture>]
type ExceptionTests() =
    [<Test>]
    member this.``Parallel_Exception_In_Line``() =
        let processor = new Processor<int>([|(fun x y -> x+y); (fun x y-> x*x);|])
        let ex = Assert.Throws<ParallelException>(fun() -> processor.executeProgram [| [|Set((0, 0), 1); Mvc((0, 0), 5)|] |] |> ignore)
        Assert.AreEqual(ParallelException(0,0), ex)

    [<Test>]
    member this.``Out of Bounds``() =
        let processor = new Processor<int>([|(fun x y -> x+y); (fun x y-> x*x);|])
        let ex = Assert.Throws<IndexOutOfBounds>(fun() ->processor.executeProgram [| [|Mvc((0, 2), 5)|] |] |> ignore)
        Assert.AreEqual(IndexOutOfBounds(0,2), ex)

[<TestFixture>]
type ProcessorRunTests() =    
    [<Test>]
    member this.``EmptyArray``() =
        let processor = new Processor<_>([|(fun x y -> x); (fun x y -> y)|])        
        processor.executeProgram [| [||]; [||] |]
        Assert.AreEqual (0, processor.ValueAt 1024 1)
    
    [<Test>]
    member this.``OneArr``() =
        let processor = new Processor<_>([|(fun x y -> x); (fun x y -> y)|])        
        processor.executeProgram [| [|Set((0, 0), 1); Mvc((0, 1), 5)|] |]
        Assert.AreEqual (0, processor.ValueAt 1024 1)
    
    [<Test>]
    member this.``ParallelTest``() =
        let processor = new Processor<int>([|(fun x y -> x + y); (fun x y -> x + y)|])
        processor.executeProgram [| [|Set((0, 0), 1); Mvc((0, 1), 5)|]; 
                         [|Set((0, 0), 2); Mvc((0, 1), 3)|];
                         [|Mov((0, 0), (0, 1))|]  |]
        Assert.AreEqual (10, processor.ValueAt 0 0)

    [<Test>]
    member this.``100500``() =
        let processor = new Processor<int>([|(fun x y -> x + y); (fun x y -> x - y); (fun x y -> x * y); (fun x y -> x / y)|])
        processor.executeProgram [| [|Set((100, 2), 1);|] |]
        Assert.AreEqual(1, processor.ValueAt 100 2)