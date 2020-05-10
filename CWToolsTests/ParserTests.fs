module ParserTests

open Expecto
open CWTools.Games
open FParsec
open CWTools.Common
open CWTools.Process
open CWTools.Parser
open CWTools.Parser.Types
open CWTools.Process.ProcessCore
open System.IO
open System.Reflection
open CWTools.Parser.DocsParser
open CWTools.Parser.SetupLogParser
open CWTools.Common.STLConstants
open System
open CWTools.Process.STLProcess
open CWTools.Localisation.STL
open CWTools.Localisation.CK2Localisation
open CWTools.Localisation
open CWTools.Utilities.Position

[<Tests>]
let tests =
    testList "parser" [
        testList "localisation parser" [
            //testCase "simple localisation test" <| fun () ->
            testCase "localisation folder" <| fun () ->
                let folder = "testfiles/localisationtests/localisation/"
                let parsed = STLLocalisationServiceFromFolder(folder).Api (STL STLLang.English)
                eprintfn "%A" parsed.Results
                ()
            testCase "localisation simple" <| fun () ->
                let parsed = STLLocalisationServiceFromFolder("testfiles/localisationtests/localisation").Api (STL STLLang.English)
                let expectedValues = dict [("required", "\"test_required\"");
                                           ("test_required_desc", "\"test_required\"");
                                           ("test_paragraph_double_newline", "\"test\\n\\ntest2\"")
                                          ]
                Expect.equal (parsed.Values |> Seq.map (fun (Microsoft.FSharp.Core.Operators.KeyValue(k,v)) -> (k,v)) |> List.ofSeq) (expectedValues |> Seq.map (fun (Microsoft.FSharp.Core.Operators.KeyValue(k,v)) -> (k,v)) |> List.ofSeq) "Not equal"
                printfn "%A" parsed.Values
                ()
            testCase "localisation CK2" <| fun () ->
                let files = ["testfiles/CK2/Localisation/SwordOfIslam.csv", File.ReadAllText "testfiles/CK2/Localisation/SwordOfIslam.csv"]
                let parsed = CK2LocalisationService(files).Api (CK2 CK2Lang.English)
                match parsed.ValueMap.TryFind "opinion_hajj_saving_on_hajj" with
                | Some value -> Expect.equal "Saved him from bandits" value.desc "Value had wrong value"
                | None -> Expect.isTrue false "Didn't find key"
                ()
            testCase "localisation CK2 - 2" <| fun () ->
                let files = ["testfiles/CK2/Localisation/v1_09.csv", File.ReadAllText("testfiles/CK2/Localisation/v1_09.csv", Text.Encoding.GetEncoding(1252))]
                let parsed = CK2LocalisationService(files).Api (CK2 CK2Lang.English)
                match parsed.ValueMap.TryFind "tp_wall_3_desc" with
                | Some value -> Expect.equal "These stone walls surround the [Root.Holder.GetHouseOfWorship] and must be overcome by any attacker." value.desc "Value had wrong value"
                | None -> Expect.isTrue false "Didn't find key"
                ()
        ] |> testLabel "localisation parsing"

        testList "jomini parser" [
            testCase "jomini simple" <| fun() ->
                let file = File.ReadAllText "testfiles/parsertests/simple.txt"
                match CKParser.parseString file "test" with
                | Success(r, _, _) ->
                    //eprintfn "%A" r
                    let node = (STLProcess.shipProcess.ProcessNode() "root" (range.Zero) r)
                    let printed = CKPrinter.api.prettyPrintStatements [node.ToRaw]
                    Expect.equal printed file "Printing shouldn't have changed string"
                | Failure(_) -> Expect.isTrue false "Parsing failure"
            // testCase "jomini fancy clause" <| fun() ->
            //     let file = File.ReadAllText "testfiles/parsertests/clause.txt"
            //     match CKParser.parseString file "test" with
            //     |Success(r, _, _) ->
            //         //eprintfn "%A" r
            //         let node = (STLProcess.shipProcess.ProcessNode() "root" (range.Zero) r)
            //         //node.All |> List.iter (eprintfn "%A")
            //         let printed = CKPrinter.api.prettyPrintStatements (node.ToRaw)
            //         Expect.equal printed file "Printing shouldn't have changed string"
        ] |> testLabel "jomini parsing"
    ]
