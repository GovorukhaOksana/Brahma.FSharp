﻿module ConsoleLauncher

open Brahma.Samples
open OpenCL.Net
open Brahma.OpenCL
open Brahma.FSharp.OpenCL.Core
open Microsoft.FSharp.Quotations
open Brahma.FSharp.OpenCL.Extensions

open System.IO
open System.Runtime.Serialization.Formatters.Binary

open TemplatesGenerator

let groups = ref 2

let maxTemplateLength = 32uy

let kRef = ref 1024    
let localWorkSizeRef = ref 512

let pathRef = ref InputGenerator.path
let templatesPathRef = ref TemplatesGenerator.path

let Main () =
    let commandLineSpecs =
        [
         "-k", ArgType.Int (fun i -> kRef := i), "Work amount for one work item."
         "-l", ArgType.Int (fun i -> localWorkSizeRef := i), "Work group size."
         "-g", ArgType.Int (fun i -> groups := i), "Work groups number."
         "-input", ArgType.String (fun s -> pathRef := s), "Input file path."
         "-templates", ArgType.String (fun s -> templatesPathRef := s), "Templates file path."
         ] |> List.map (fun (shortcut, argtype, description) -> ArgInfo(shortcut, argtype, description))
    ArgParser.Parse commandLineSpecs

    let k = !kRef  
    let localWorkSize = !localWorkSizeRef

    let length = k * localWorkSize * !groups

    let path = !pathRef
    let templatesPath = !templatesPathRef

    let templatesReader = File.OpenRead(templatesPath)
    let formatter = new BinaryFormatter()
    let deserialized = formatter.Deserialize(templatesReader)

    templatesReader.Close()

    let templatesRef = ref 0
    let templateLengthsRef = ref null
    let templatesSumRef = ref 0
    let templateArrRef = ref null

    match deserialized with
    | :? Templates as t -> 
        templatesRef := t.number
        templateLengthsRef := t.sizes
        templatesSumRef := t.content.Length
        templateArrRef := t.content
    | other -> failwith "Deserialized object is not a Templates struct!"

    let templates = !templatesRef
    let templateLengths = !templateLengthsRef
    let templatesSum = !templatesSumRef
    let templateArr = !templateArrRef

    printfn "Running %A groups with %A items in each, %A in total." !groups localWorkSize (localWorkSize * !groups)
    printfn "Each item will process %A bytes of input, %A total on each iteration." k (localWorkSize * !groups * k)
    printfn ""

    let buffer = Array.zeroCreate length

    let readingTimer = new Timer<string>()

    let testAlgorithm initializer getter label counter =
        readingTimer.Start()
        let mutable read = 0
        let mutable lowBound = 0
        let mutable highBound = 0

        let mutable current = 0L

        let reader = new FileStream(path, FileMode.Open)
        let bound = reader.Length

        let prefix = NaiveSearch.findPrefixes templates maxTemplateLength templateLengths templateArr
        initializer()

        while current < bound do
            if current > 0L then
                System.Array.Copy(buffer, (read + lowBound - (int) maxTemplateLength), buffer, 0, (int) maxTemplateLength)
                lowBound <- (int) maxTemplateLength

            highBound <- (if (int64) (length - lowBound) < bound then (length - lowBound) else (int) bound)
            read <- reader.Read(buffer, lowBound, highBound)
            current <- current + (int64) read

            let mutable countingBound = read + lowBound
            let mutable matchBound = read + lowBound
            if current < bound then
                countingBound <- countingBound - (int) maxTemplateLength

            counter := !counter + NaiveSearch.countMatches (getter()) countingBound matchBound templateLengths prefix
    
        reader.Close()
        readingTimer.Lap(label)

    let testAlgorithmAsync initializer uploader downloader label counter =
        readingTimer.Start()
        let mutable read = 0
        let mutable lowBound = 0
        let mutable highBound = 0

        let mutable current = 0L

        let reader = new FileStream(path, FileMode.Open)
        let bound = reader.Length

        let prefix = NaiveSearch.findPrefixes templates maxTemplateLength templateLengths templateArr
        initializer()

        let mutable countingBound = 0
        let mutable matchBound = 0

        while current < bound do
            if current > 0L then
                System.Array.Copy(buffer, (read + lowBound - (int) maxTemplateLength), buffer, 0, (int) maxTemplateLength)
                lowBound <- (int) maxTemplateLength

            highBound <- (if (int64) (length - lowBound) < bound then (length - lowBound) else (int) bound)
            read <- reader.Read(buffer, lowBound, highBound)

            if current > 0L then
                counter := !counter + NaiveSearch.countMatches (downloader()) countingBound matchBound templateLengths prefix

            current <- current + (int64) read

            countingBound <- read + lowBound
            matchBound <- read + lowBound
            if current < bound then
                countingBound <- countingBound - (int) maxTemplateLength

            uploader()
        
        counter := !counter + NaiveSearch.countMatches (downloader()) countingBound matchBound templateLengths prefix

        reader.Close()
        readingTimer.Lap(label)

    let cpuMatchesInitilizer = (fun () -> ())
    let cpuMatchesHashedInitilizer = (fun () -> ())
    let gpuMatchesInitilizer = (fun () -> NaiveSearchGpu.initialize length k localWorkSize templates templateLengths buffer templateArr)
    let gpuMatchesHashingInitilizer = (fun () -> NaiveHashingSearchGpu.initialize length maxTemplateLength k localWorkSize templates templatesSum templateLengths buffer templateArr)
    let gpuMatchesLocalInitilizer = (fun () -> NaiveSearchGpuLocalTemplates.initialize length k localWorkSize templates templateLengths buffer templatesSum templateArr)
    let gpuMatchesHashingPrivateInitilizer = (fun () -> NaiveHashingSearchGpuPrivate.initialize length maxTemplateLength k localWorkSize templates templatesSum templateLengths buffer templateArr)
    let gpuMatchesHashingPrivateLocalInitilizer = (fun () -> NaiveHashingGpuPrivateLocal.initialize length maxTemplateLength k localWorkSize templates templatesSum templateLengths buffer templateArr)

    let cpuMatchesGetter = (fun () -> NaiveSearch.findMatches length templates templateLengths buffer templateArr)
    let cpuMatchesHashedGetter = (fun () -> NaiveHashingSearch.findMatches length maxTemplateLength templates templatesSum templateLengths buffer templateArr)
//    let gpuMatchesGetter = (fun () -> NaiveSearchGpu.getMatches())
//    let gpuMatchesHashingGetter = (fun () -> NaiveHashingSearchGpu.getMatches())
//    let gpuMatchesLocalGetter = (fun () -> NaiveSearchGpuLocalTemplates.getMatches())
//    let gpuMatchesHashingPrivateGetter = (fun () -> NaiveHashingSearchGpuPrivate.getMatches())
//    let gpuMatchesHashingPrivateLocalGetter = (fun () -> NaiveHashingGpuPrivateLocal.getMatches())

    let gpuMatchesUploader = (fun () -> NaiveSearchGpu.upload())
    let gpuMatchesHashingUploader = (fun () -> NaiveHashingSearchGpu.upload())
    let gpuMatchesLocalUploader = (fun () -> NaiveSearchGpuLocalTemplates.upload())
    let gpuMatchesHashingPrivateUploader = (fun () -> NaiveHashingSearchGpuPrivate.upload())
    let gpuMatchesHashingPrivateLocalUploader = (fun () -> NaiveHashingGpuPrivateLocal.upload())

    let gpuMatchesDownloader = (fun () -> NaiveSearchGpu.download())
    let gpuMatchesHashingDownloader = (fun () -> NaiveHashingSearchGpu.download())
    let gpuMatchesLocalDownloader = (fun () -> NaiveSearchGpuLocalTemplates.download())
    let gpuMatchesHashingPrivateDownloader = (fun () -> NaiveHashingSearchGpuPrivate.download())
    let gpuMatchesHashingPrivateLocalDownloader = (fun () -> NaiveHashingGpuPrivateLocal.download())

    let cpuMatches = ref 0  
    let cpuMatchesHashed = ref 0
    let gpuMatches = ref 0
    let gpuMatchesHashing = ref 0
    let gpuMatchesLocal = ref 0
    let gpuMatchesHashingPrivate = ref 0
    let gpuMatchesHashingPrivateLocal = ref 0

    testAlgorithm cpuMatchesInitilizer cpuMatchesGetter NaiveSearch.label cpuMatches
    testAlgorithm cpuMatchesHashedInitilizer cpuMatchesHashedGetter NaiveHashingSearch.label cpuMatchesHashed
    testAlgorithmAsync gpuMatchesInitilizer gpuMatchesUploader gpuMatchesDownloader NaiveSearchGpu.label gpuMatches
    testAlgorithmAsync gpuMatchesHashingInitilizer gpuMatchesHashingUploader gpuMatchesHashingDownloader NaiveHashingSearchGpu.label gpuMatchesHashing
    testAlgorithmAsync gpuMatchesLocalInitilizer gpuMatchesLocalUploader gpuMatchesLocalDownloader NaiveSearchGpuLocalTemplates.label gpuMatchesLocal
    testAlgorithmAsync gpuMatchesHashingPrivateInitilizer gpuMatchesHashingPrivateUploader gpuMatchesHashingPrivateDownloader NaiveHashingSearchGpuPrivate.label gpuMatchesHashingPrivate
    testAlgorithmAsync gpuMatchesHashingPrivateLocalInitilizer gpuMatchesHashingPrivateLocalUploader gpuMatchesHashingPrivateLocalDownloader NaiveHashingGpuPrivateLocal.label gpuMatchesHashingPrivateLocal

    Substrings.verifyResults !cpuMatches !cpuMatchesHashed NaiveHashingSearch.label
    Substrings.verifyResults !cpuMatches !gpuMatches NaiveSearchGpu.label
    Substrings.verifyResults !cpuMatches !gpuMatchesHashing NaiveHashingSearchGpu.label
    Substrings.verifyResults !cpuMatches !gpuMatchesLocal NaiveSearchGpuLocalTemplates.label
    Substrings.verifyResults !cpuMatches !gpuMatchesHashingPrivate NaiveHashingSearchGpuPrivate.label
    Substrings.verifyResults !cpuMatches !gpuMatchesHashingPrivateLocal NaiveHashingGpuPrivateLocal.label

    printfn ""

    printfn "Raw computation time spent:"
    
    FileReading.printGlobalTime NaiveSearch.label
    FileReading.printGlobalTime NaiveHashingSearch.label
    FileReading.printGlobalTime NaiveSearchGpu.label
    FileReading.printGlobalTime NaiveHashingSearchGpu.label
    FileReading.printGlobalTime NaiveSearchGpuLocalTemplates.label
    FileReading.printGlobalTime NaiveHashingSearchGpuPrivate.label
    FileReading.printGlobalTime NaiveHashingGpuPrivateLocal.label

    printfn ""

    printfn "Computation time with preparations:"
    FileReading.printGlobalTime NaiveSearch.label
    FileReading.printTime NaiveHashingSearch.timer NaiveHashingSearch.label
    FileReading.printTime NaiveSearchGpu.timer NaiveSearchGpu.label
    FileReading.printTime NaiveHashingSearchGpu.timer NaiveHashingSearchGpu.label
    FileReading.printTime NaiveSearchGpuLocalTemplates.timer NaiveSearchGpuLocalTemplates.label
    FileReading.printTime NaiveHashingSearchGpuPrivate.timer NaiveHashingSearchGpuPrivate.label
    FileReading.printTime NaiveHashingGpuPrivateLocal.timer NaiveHashingGpuPrivateLocal.label

    printfn ""

    printfn "Total time with reading:"
    FileReading.printTime readingTimer NaiveSearch.label
    FileReading.printTime readingTimer NaiveHashingSearch.label
    FileReading.printTime readingTimer NaiveSearchGpu.label
    FileReading.printTime readingTimer NaiveHashingSearchGpu.label
    FileReading.printTime readingTimer NaiveSearchGpuLocalTemplates.label
    FileReading.printTime readingTimer NaiveHashingSearchGpuPrivate.label
    FileReading.printTime readingTimer NaiveHashingGpuPrivateLocal.label

    ignore (System.Console.Read())

do Main()