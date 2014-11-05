﻿module Brahman.Substrings.Matcher

open Brahma.Helpers
open Brahma.OpenCL
//open OpenCL.Net
open Brahma.FSharp.OpenCL.Core
open Microsoft.FSharp.Quotations
open Brahma.FSharp.OpenCL.Extensions
open Brahma.FSharp.OpenCL.Translator.Common
open System.Threading.Tasks

type Config =
    {
        additionalArgs: uint64
        additionalTempData: uint64
        localWorkSize: int
        chunkSize: int
        groups: int
        groupSize: int
        bufLength: int
    }

type Templates = {
    number : int
    sizes : byte[]
    content : byte[]
    }

[<Struct>]
type MatchRes =
    val ChunkNum: int
    val Offset: int
    val PatternId : int
    new (chunkNum,offset,patternId) = {ChunkNum = chunkNum; Offset = offset; PatternId = patternId }

[<Struct>]
type FindRes =
    val Data: array<MatchRes>
    val Templates: array<array<byte>>
    val ChunkSize: int
    new (data,templates,chunkSize) = {Data = data; Templates = templates; ChunkSize = chunkSize }

type Matcher(?maxHostMem) =    
    let totalResult = new ResizeArray<_>()
    let mutable label = ""
    let platformName = "NVIDIA*"
    let deviceType = OpenCL.Net.DeviceType.Default    

    let provider =
        try  ComputeProvider.Create(platformName, deviceType)
        with 
        | ex -> failwith ex.Message

    let commandQueue = new CommandQueue(provider, provider.Devices |> Seq.head)

    let timer = new Timer<string>()

    let maxTemplateLength = 32       
    
    let mutable buffersCreated = false
    let mutable result = [||]
    let mutable input = [||]
    let c = [|0|]
    let mutable ready = true

    let memory,ex = OpenCL.Net.Cl.GetDeviceInfo(provider.Devices |> Seq.head, OpenCL.Net.DeviceInfo.MaxMemAllocSize)
    let maxGpuMemory = memory.CastTo<uint64>()

    let maxHostMemory = match maxHostMem with Some x -> x | _ -> 256UL * 1024UL * 1024UL

    let configure (templates:array<_>) =
        let tLenghth = templates |> Array.length |> uint64
        let additionalArgs = 2UL * (256UL + 2UL) * (uint64) maxTemplateLength * tLenghth + tLenghth +
                             tLenghth + 13UL + 1000UL

        let additionalTempData = 2UL * (256UL + 256UL + 3UL) * (uint64) maxTemplateLength * tLenghth + 
                                 (uint64) maxTemplateLength * tLenghth + 100000UL

        let availableMemory = (int) (min (maxGpuMemory - additionalArgs) (maxHostMemory - additionalArgs - additionalTempData))
        let lws,ex = OpenCL.Net.Cl.GetDeviceInfo(provider.Devices |> Seq.head, OpenCL.Net.DeviceInfo.MaxWorkGroupSize)
        let localWorkSize = int <| lws.CastTo<uint64>()
        let chunkSize = 256
        let groupSize = chunkSize * localWorkSize * (1 + 2)
        let groups = availableMemory / groupSize
        let length = chunkSize * localWorkSize * groups
        {
            additionalArgs = additionalArgs
            additionalTempData = additionalTempData
            localWorkSize = localWorkSize
            chunkSize = chunkSize
            groups = groups
            groupSize = groupSize
            bufLength = length
        }

    let printConfiguration config =
        printfn
            "Maximum memory on GPU is %A, additional args size is %A, temp data size is %A"
            maxGpuMemory config.additionalArgs config.additionalTempData

        printfn 
            "Running %A groups with %A items in each, %A in total."
            config.groups config.localWorkSize (config.localWorkSize * config.groups)
        printfn "Each item will process %A bytes of input, %A total on each iteration." config.chunkSize config.bufLength
        printfn ""
    
    let initialize config templates command =
        totalResult.Clear()
        timer.Reset()
        timer.Start()
        printConfiguration config 
        result <- Array.zeroCreate config.bufLength
        input <- Array.zeroCreate config.bufLength
        let kernel, kernelPrepare, kernelRun = provider.Compile(query=command, translatorOptions=[BoolAsBit])                
        let l = (config.bufLength + (config.chunkSize-1))/config.chunkSize 
        let d = new _1D(l,config.localWorkSize)
        timer.Lap(label)
        kernel, (kernelPrepare d), kernelRun

    let readingTimer = new Timer<string>()
    let countingTimer = new Timer<string>()   

    let close () =     
        provider.CloseAllBuffers()
        buffersCreated <- false

    let downloader label (task:Task<unit>) =
        if ready then failwith "Not running, can't download!"
        ready <- true

        task.Wait()
        ignore (commandQueue.Add(c.ToHost provider).Finish())
        ignore (commandQueue.Add(c.ToGpu(provider, [|0|])).Finish())
        ignore (commandQueue.Add(result.ToHost(provider)).Finish())
        buffersCreated <- true
        Timer<string>.Global.Lap(label)
        timer.Lap(label)

        result

    let uploader (kernelRun:_ -> Commands.Run<_>) =
        if not ready then failwith "Already running, can't upload!"
        ready <- false

        timer.Start()
        Timer<string>.Global.Start()
        if buffersCreated || (provider.AutoconfiguredBuffers <> null && provider.AutoconfiguredBuffers.ContainsKey(input)) then
            ignore (commandQueue.Add(input.ToGpu provider).Finish())
            async { ignore (commandQueue.Add(kernelRun()).Finish())}
            |> Async.StartAsTask
        else
            ignore (commandQueue.Add(kernelRun()).Finish())
            async {()} |> Async.StartAsTask

    let countMatchesDetailed index (result:array<uint16>) maxTemplateLength bound length (templateLengths:array<byte>) (prefix:array<int16>) (matchesArray:array<uint64>) offset =
        let mutable matches = 0
        let clearBound = min (bound - 1) (length - (int) maxTemplateLength)        
        let mutable resultOffset = 0
        while resultOffset <= c.[0] - 3  do
            let i = int ((uint32 result.[resultOffset] <<< 16) ||| uint32 result.[resultOffset+1])
            let mutable matchIndex = result.[resultOffset+2]                            
            if 0 < i && i < clearBound
            then
                matchesArray.[(int) matchIndex] <- matchesArray.[(int) matchIndex] + 1UL
                totalResult.Add(new MatchRes(index, i, int matchIndex))
                matches <- matches + 1
            else           
                while matchIndex >= 0us && i + (int) templateLengths.[(int) matchIndex] > length do
                    matchIndex <- uint16 prefix.[(int) matchIndex]                                            
                matchesArray.[(int) matchIndex] <- matchesArray.[(int) matchIndex] + 1UL
                totalResult.Add(new MatchRes(index, i, int matchIndex))
                matches <- matches + 1
            resultOffset <- resultOffset + 3

        matches

    let printResult (templates:Templates) (matches:array<_>) counter =
        let hex = Array.map (fun (x : byte) -> System.String.Format("{0:X2} ", x)) templates.content
        let mutable start = 0
        for i in 0..(templates.number - 1) do
            let pattern = System.String.Concat(Array.sub hex start ((int) templates.sizes.[i]))
            printfn "%A: %A matches found by %A" pattern matches.[i] label
            start <- start + (int) templates.sizes.[i]

        printfn ""

        printfn "Total found by %A: %A" label counter

    let run command (readFun: byte[] -> Option<byte[]>) templates prefix label close =
        let counter = ref 0
        readingTimer.Start()
        
        let matches = Array.zeroCreate 512
                
        let mutable countingBound = 0
        let mutable matchBound = 0

        let mutable task = Unchecked.defaultof<Task<unit>>

        let mutable index = 0
        let mutable totalIndex = 0
        let mutable current = 0L

        let isLastChunk = ref false

        while not !isLastChunk do
            let last = readFun input
            isLastChunk := last.IsNone
            let read = match last with Some x -> x.Length | _ -> -1
            if current > 0L then
                let result = downloader label task
                countingTimer.Start()
                counter := 
                    !counter 
                    + countMatchesDetailed (totalIndex-1) result maxTemplateLength countingBound matchBound templates.sizes prefix matches (current - (int64) matchBound + 512L)
                countingTimer.Lap(label)

            if (read > 0) then
                index <- index + 1
                totalIndex <- totalIndex + 1
                current <- current + (int64) read

                if index = 50 then
                    printfn "I am %A and I've already read %A bytes!" label current
                    index <- 0

                countingBound <- read
                matchBound <- read
                task <- uploader command

        printResult templates matches !counter
        readingTimer.Lap(label)
        close()

    let prepareTemplates array = 
        let sorted = Array.sortBy (fun (a:byte[]) -> a.Length) array
        let lengths = Array.map (fun (a:byte[]) -> (byte) a.Length) sorted
        let templateBytes = Array.toSeq sorted |> Array.concat
        let readyTemplates = { number = sorted.Length; sizes = lengths; content = templateBytes;}
        readyTemplates

    let finalize () =
        close ()
        printfn "Computation time with preparations:"
        Helpers.printTime timer label

        printfn ""

        printfn "Total time with reading:"
        Helpers.printTime readingTimer label

        printfn ""

        printfn "Counting time:"
        Helpers.printTime countingTimer label

    let sorted (templates:Templates) = 
        let start = ref 0
        [|for i in 0..(templates.number - 1) do
            let pattern = Array.sub templates.content !start ((int) templates.sizes.[i])
            start := !start + (int) templates.sizes.[i]
            yield pattern
        |]

    let rk readFun templateArr  = 
        label <- RabinKarp.label
        let config = configure templateArr
        let templates = prepareTemplates templateArr
        let kernel, kernelPrepare, kernelRun = initialize config templateArr RabinKarp.command        
        let prefix, next, leaf, _ = Helpers.buildSyntaxTree templates.number (int maxTemplateLength) templates.sizes templates.content        
        let templateHashes = Helpers.computeTemplateHashes templates.number templates.content.Length templates.sizes templates.content
        kernelPrepare 
            config.bufLength config.chunkSize templates.number templates.sizes templateHashes maxTemplateLength input templates.content result c
        run kernelRun readFun templates prefix label close
        timer.Lap(label)
        finalize()
        c.[0] <- 0
        new FindRes(totalResult.ToArray(), sorted templates, input.Length)

    new () = Matcher (256UL * 1024UL * 1024UL)

    (*member this.NaiveSearch (hdId, templateArr)  = 
        label <- NaiveSearch.label
        let config = configure templateArr
        let templates = prepareTemplates templateArr
        let kernel, kernelPrepare, kernelRun = initialize config templateArr NaiveSearch.command
        let prefix, next, leaf, _ = Helpers.buildSyntaxTree templates.number (int maxTemplateLength) templates.sizes templates.content
        kernelPrepare config.bufLength config.chunkSize templates.number templates.sizes input templates.content result
        run kernelRun hdId templates prefix label close
        timer.Lap(label)
        finalize()
        new FindRes(totalResult.ToArray(), sorted templates, input.Length)
        *)
//    member this.AhoCorasik (hdId, templateArr)  =        
//        label <- AhoCorasick.label
//        let config = configure templateArr
//        let templates = prepareTemplates templateArr
//        let kernel, kernelPrepare, kernelRun = initialize config templateArr AhoCorasick.command        
//        let prefix, next, leaf, _ = Helpers.buildSyntaxTree templates.number (int maxTemplateLength) templates.sizes templates.content
//        let go, _, exit = AhoCorasick.buildStateMachine templates.number maxTemplateLength next leaf
//        kernelPrepare config.bufLength config.chunkSize templates.number templates.sizes  go exit leaf maxTemplateLength input templates.content result
//        run kernelRun hdId templates config prefix label close
//        timer.Lap(label)
//        finalize()

   (* member this.Hashtable (hdId, templateArr)  = 
        label <- Hashtables.label
        let config = configure templateArr
        let templates = prepareTemplates templateArr
        let kernel, kernelPrepare, kernelRun = initialize config templateArr Hashtables.command        
        let prefix, _, _, _ = Helpers.buildSyntaxTree templates.number (int maxTemplateLength) templates.sizes templates.content        
        let starts = Hashtables.computeTemplateStarts templates.number templates.sizes
        let templateHashes = Helpers.computeTemplateHashes templates.number templates.content.Length templates.sizes templates.content
        let table, next = Hashtables.createHashTable templates.number templates.sizes templateHashes
        kernelPrepare 
            config.bufLength config.chunkSize templates.number templates.sizes  templateHashes table next starts maxTemplateLength input templates.content result
        run kernelRun hdId templates prefix label close
        timer.Lap(label)
        finalize()
        new FindRes(totalResult.ToArray(), sorted templates, input.Length)*)

    member this.RabinKarp (readFun, templateArr) = 
        rk readFun templateArr

    member this.RabinKarp (hdId, templateArr) =         
        let handle = RawIO.CreateFileW hdId        
        let res = rk (RawIO.ReadHD handle) templateArr
        RawIO.CloseHandle(handle)
        |> ignore
        res

    member this.RabinKarp (inSeq, templateArr) = 
        let readF = 
            let next = Helpers.chunk 32 inSeq
            let finish = ref false
            fun buf ->
                if !finish
                then None
                else
                    let r = next buf
                    match r with
                    | None -> ()
                    | Some x -> finish := true
                    Some buf

        rk readF templateArr

    member this.InBufSize with get () = input.Length