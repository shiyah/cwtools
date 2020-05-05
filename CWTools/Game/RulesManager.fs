namespace CWTools.Games
open CWTools.Rules
open CWTools.Common
open CWTools.Utilities.Position
open FSharp.Collections.ParallelSeq
open CWTools.Process.Localisation
open CWTools.Process.Scopes
open CWTools.Utilities.Utils
open CWTools.Rules.RulesHelpers
open System.IO
open CWTools.Common.NewScope
type RulesSettings = {
    ruleFiles : (string * string) list
    validateRules : bool
    debugRulesOnly : bool
    debugMode : bool
}

type LocalisationEmbeddedSettings =
| Legacy of (string * (Scope list)) list * string list
| Jomini of CWTools.Parser.DataTypeParser.JominiLocDataTypes


type EmbeddedSettings = {
    triggers : DocEffect list
    effects : DocEffect list
    embeddedFiles : (string * string) list
    modifiers : ActualModifier list
    cachedResourceData : (Resource * Entity) list
    localisationCommands : LocalisationEmbeddedSettings
    eventTargetLinks : EventTargetLink list
    cachedRuleMetadata : CachedRuleMetadata option
}

type RuleManagerSettings<'T, 'L when 'T :> ComputedData and 'L :> Lookup> = {
    rulesSettings : RulesSettings option
    parseScope : string -> Scope
    allScopes : Scope list
    anyScope : Scope
    changeScope : ChangeScope
    defaultContext : ScopeContext
    defaultLang : Lang
    oneToOneScopesNames : string list
    loadConfigRulesHook : RootRule list -> 'L -> EmbeddedSettings -> RootRule list
    refreshConfigBeforeFirstTypesHook : 'L -> IResourceAPI<'T> -> EmbeddedSettings -> unit
    refreshConfigAfterFirstTypesHook : 'L -> IResourceAPI<'T> -> EmbeddedSettings -> unit
    refreshConfigAfterVarDefHook : 'L -> IResourceAPI<'T> -> EmbeddedSettings -> unit
    processLocalisation : 'L -> (Lang * Collections.Map<string,CWTools.Localisation.Entry> -> Lang * Collections.Map<string,LocEntry>)
    validateLocalisation : 'L -> (LocEntry -> ScopeContext -> CWTools.Validation.ValidationResult)
}



type RulesManager<'T, 'L when 'T :> ComputedData and 'L :> Lookup>
    (resources : IResourceAPI<'T>, lookup : 'L,
     settings : RuleManagerSettings<'T, 'L>,
     localisation : LocalisationManager<'T>,
     embeddedSettings : EmbeddedSettings,
     languages : Lang list,
     debugMode : bool) =

    let addEmbeddedTypeDefData =
        match embeddedSettings.cachedRuleMetadata with
        | None -> id
        | Some md ->
            fun (newMap : Map<string,list<TypeDefInfo>>) ->
                Map.fold (fun s k v ->
                    match Map.tryFind k s with
                    | Some v' -> Map.add k (v@v') s
                    | None -> Map.add k v s) newMap md.typeDefs

    let addEmbeddedEnumDefData =
        match embeddedSettings.cachedRuleMetadata with
        | None -> id
        | Some md ->
            fun (newMap : Map<string,string * list<string>>) ->
                Map.fold (fun s k (d, v) ->
                    match Map.tryFind k s with
                    | Some (d', v') -> Map.add k (d, v@v') s
                    | None -> Map.add k (d, v) s) newMap md.enumDefs

    let addEmbeddedVarDefData =
        match embeddedSettings.cachedRuleMetadata with
        | None -> id
        | Some md ->
            fun (newMap : Map<string,list<string * range>>) ->
                Map.fold (fun s k v ->
                    match Map.tryFind k s with
                    | Some v' -> Map.add k (v@v') s
                    | None -> Map.add k v s) newMap md.varDefs

    let addEmbeddedLoc (langs) =
        match embeddedSettings.cachedRuleMetadata with
        | None -> id
        | Some md ->
            fun (newList : (Lang * Set<string>) list) ->
                let newMap = newList |> Map.ofList
                let oldList = md.loc |> List.filter (fun (l, _) -> List.contains l langs)
                let embeddedMap = oldList |> Map.ofList
                let res =
                    Map.fold (fun s k v ->
                    match Map.tryFind k s with
                    | Some v' -> Map.add k (Set.union v  v') s
                    | None -> Map.add k v s) newMap embeddedMap
                res |> Map.toList

    let addEmbeddedFiles =
        match embeddedSettings.cachedRuleMetadata with
        | None -> id
        | Some md ->
            fun (newSet : Set<string>) -> Set.union newSet md.files


    let mutable tempEffects = []
    let mutable tempTriggers = []
    let mutable simpleEnums = []
    let mutable complexEnums = []
    let mutable tempTypes = []
    let mutable tempValues = Map.empty
    let mutable tempTypeMap = [("", StringSet.Empty(InsensitiveStringComparer()))] |> Map.ofList
    let mutable tempEnumMap = [("", ("", StringSet.Empty(InsensitiveStringComparer())))] |> Map.ofList
    let mutable rulesDataGenerated = false

    let expandPredefinedValues (types : Map<string, _>) (enums : Map<string, _ * list<string>>) (values : string list) =
        let replaceType (value : string) =
            let startIndex = value.IndexOf "<"
            let endIndex = value.IndexOf ">" - 1
            let referencedType = value.Substring(startIndex + 1, (endIndex - startIndex))
            match types |> Map.tryFind referencedType with
            | Some typeValues ->
                // eprintfn "epv %A %A %A %A" value typeValues (value.Substring(0, startIndex)) (value.Substring(endIndex + 2))
                let res = typeValues |> Seq.map (fun tv -> value.Substring(0, startIndex) + tv + value.Substring(endIndex + 2)) |> List.ofSeq
                // eprintfn "epv2 %A" res
                res
            | None -> [value]
        let replaceEnum (value : string) =
            let startIndex = value.IndexOf "enum["
            let endIndex = value.IndexOf "]" - 1
            let referencedEnum = value.Substring(startIndex + 5, (endIndex - (startIndex + 4)))
            match enums |> Map.tryFind referencedEnum with
            | Some (_, enumValues) ->
                let res = enumValues |> Seq.map (fun tv -> value.Substring(0, startIndex) + tv + value.Substring(endIndex + 2)) |> List.ofSeq
                // eprintfn "epv2 %A" res
                res
            | None -> [value]
        values |> List.collect (fun v -> if v.Contains "<" && v.Contains ">" then replaceType v else [v])
               |> List.collect (fun v -> if v.Contains "enum[" && v.Contains "]" then replaceEnum v else [v])

    let loadBaseConfig(rulesSettings : RulesSettings) =
        let rules, types, enums, complexenums, values =
            rulesSettings.ruleFiles
                |> List.filter (fun (fn, ft) -> Path.GetExtension fn == ".cwt" )
                |> CWTools.Rules.RulesParser.parseConfigs settings.parseScope settings.allScopes settings.anyScope
        // tempEffects <- updateScriptedEffects game rules
        // effects <- tempEffects
        // tempTriggers <- updateScriptedTriggers game rules
        // _triggers <- tempTriggers
        lookup.typeDefs <- types
        // let rulesWithMod = rules @ addModifiersWithScopes(game)
        let rulesPostHook = settings.loadConfigRulesHook rules lookup embeddedSettings
        // lookup.configRules <- rulesWithMod
        lookup.configRules <- rulesPostHook
        simpleEnums <- enums
        complexEnums <- complexenums
        tempTypes <- types
        tempValues <- values |> Map.ofList //|> List.map (fun (s, sl) -> s, (sl |> List.map (fun s2 -> s2, range.Zero))) |> Map.ofList
        rulesDataGenerated <- false
        // log (sprintf "Update config rules def: %i" timer.ElapsedMilliseconds); timer.Restart()

    let debugChecks() =
        if debugMode
        then
            // let filesWithoutTypes = getEntitiesWithoutTypes lookup.typeDefs (resources.AllEntities() |> List.map (fun struct(e,_) -> e))
            // filesWithoutTypes |> List.iter (eprintfn "File without type %s")
            ()
        else ()
    let refreshConfig() =
        /// Enums
        let complexEnumDefs = getEnumsFromComplexEnums complexEnums (resources.AllEntities() |> List.map (fun struct(e,_) -> e))
        // let modifierEnums = { key = "modifiers"; values = lookup.coreModifiers |> List.map (fun m -> m.Tag); description = "Modifiers" }
        let allEnums = simpleEnums @ complexEnumDefs// @ [modifierEnums] @ [{ key = "provinces"; description = "provinces"; values = lookup.CK2provinces}]

        let newEnumDefs = allEnums |> List.map (fun e -> (e.key, (e.description, e.values))) |> Map.ofList
        lookup.enumDefs <- addEmbeddedEnumDefData newEnumDefs

        settings.refreshConfigBeforeFirstTypesHook lookup resources embeddedSettings

        tempEnumMap <- lookup.enumDefs |> Map.toSeq |> PSeq.map (fun (k, (d, s)) -> k, (d, StringSet.Create(InsensitiveStringComparer(), (s)))) |> Map.ofSeq

        /// First pass type defs
        let loc = addEmbeddedLoc languages (localisation.localisationKeys)
        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        let files = addEmbeddedFiles (resources.GetFileNames() |> Set.ofList)
        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        let allentities = resources.AllEntities() |> List.map (fun struct(e,_) -> e)
        let refreshTypeInfo() =
            let tempRuleValidationService = RuleValidationService(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, Collections.Map.empty, loc, files, lookup.eventTargetLinksMap, lookup.valueTriggerMap , settings.anyScope, settings.changeScope, settings.defaultContext, settings.defaultLang, (settings.processLocalisation lookup), (settings.validateLocalisation lookup))
            let typeDefInfo = getTypesFromDefinitions (Some tempRuleValidationService) tempTypes allentities
            let newTypeDefInfo = typeDefInfo
            lookup.typeDefInfo <- addEmbeddedTypeDefData newTypeDefInfo// |> Map.map (fun _ v -> v |> List.map (fun (_, t, r) -> (t, r)))
            let newTypeMap = lookup.typeDefInfo |> Map.toSeq |> PSeq.map (fun (k, s) -> k, StringSet.Create(InsensitiveStringComparer(), (s |> List.map (fun tdi -> tdi.id)))) |> Map.ofSeq
            newTypeMap
        let timer = System.Diagnostics.Stopwatch()
        timer.Start()
        let mutable i = 0
        let step() =
            //log "%A" current
            i <- i + 1
            let before = tempTypeMap
            tempTypeMap <- refreshTypeInfo()
            log (sprintf "Refresh types time: %i" timer.ElapsedMilliseconds); timer.Restart()
            tempTypeMap = before || i > 5
        while (not(step())) do ()

        let tempRuleValidationService = RuleValidationService(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, Collections.Map.empty, loc, files, lookup.eventTargetLinksMap, lookup.valueTriggerMap , settings.anyScope, settings.changeScope, settings.defaultContext, settings.defaultLang, (settings.processLocalisation lookup), (settings.validateLocalisation lookup))

        lookup.typeDefInfoForValidation <- lookup.typeDefInfo |> Map.map (fun _ v -> v |> List.choose (fun (tdi) -> if tdi.validate then Some (tdi.id, tdi.range) else None))
        settings.refreshConfigAfterFirstTypesHook lookup resources embeddedSettings
        tempTypeMap <- lookup.typeDefInfo |> Map.toSeq |> PSeq.map (fun (k, s) -> k, StringSet.Create(InsensitiveStringComparer(), (s |> List.map (fun tdi -> tdi.id)))) |> Map.ofSeq
        let tempInfoService = (InfoService(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, Collections.Map.empty, loc, files, lookup.eventTargetLinksMap, lookup.valueTriggerMap, tempRuleValidationService, settings.changeScope, settings.defaultContext, settings.anyScope, settings.defaultLang, (settings.processLocalisation lookup), (settings.validateLocalisation lookup)))


        //let infoService = tempInfoService
        // game.InfoService <- Some tempInfoService
        if not rulesDataGenerated then resources.ForceRulesDataGenerate(); rulesDataGenerated <- true else ()
        let predefValues = tempValues |> Map.map (fun k vs -> (expandPredefinedValues tempTypeMap lookup.enumDefs vs) )
                                      |> Map.toList |> List.map (fun (s, sl) -> s, (sl |> List.map (fun s2 -> s2, range.Zero))) |> Map.ofList

        let results = resources.AllEntities() |> PSeq.map (fun struct(e, l) -> (l.Force().Definedvariables |> (Option.defaultWith (fun () -> tempInfoService.GetDefinedVariables e))))
                        |> Seq.fold (fun m map -> Map.toList map |>  List.fold (fun m2 (n,k) -> if Map.containsKey n m2 then Map.add n ((k |> List.ofSeq)@m2.[n]) m2 else Map.add n (k |> List.ofSeq) m2) m) predefValues

        lookup.varDefInfo <- addEmbeddedVarDefData results
        // eprintfn "vdi %A" results
        let results = resources.AllEntities() |> PSeq.map (fun struct(e, l) -> (l.Force().SavedEventTargets |> (Option.defaultWith (fun () -> tempInfoService.GetSavedEventTargets e))))
                        |> Seq.fold (fun (acc : ResizeArray<_>) e -> acc.AddRange((e)); acc ) (new ResizeArray<_>())
        lookup.savedEventTargets <- results
                        //|> Seq.fold (fun m map -> Map.toList map |>  List.fold (fun m2 (n,k) -> if Map.containsKey n m2 then Map.add n ((k |> List.ofSeq)@m2.[n]) m2 else Map.add n (k |> List.ofSeq) m2) m) tempValues
        settings.refreshConfigAfterVarDefHook lookup resources embeddedSettings

        let varMap = lookup.varDefInfo |> Map.toSeq |> PSeq.map (fun (k, s) -> k, StringSet.Create(InsensitiveStringComparer(), (List.map fst s))) |> Map.ofSeq

        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        let dataTypes = embeddedSettings.localisationCommands |> function | Jomini dts -> dts | _ -> { promotes = Map.empty; functions = Map.empty; dataTypes = Map.empty; dataTypeNames = Set.empty }

        let completionService = (CompletionService(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, varMap, loc, files, lookup.eventTargetLinksMap, lookup.valueTriggerMap, [], settings.changeScope, settings.defaultContext, settings.anyScope, settings.oneToOneScopesNames, settings.defaultLang, dataTypes, (settings.processLocalisation lookup), (settings.validateLocalisation lookup)))
        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        let ruleValidationService =  (RuleValidationService(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, varMap, loc, files, lookup.eventTargetLinksMap, lookup.valueTriggerMap, settings.anyScope, settings.changeScope, settings.defaultContext, settings.defaultLang, (settings.processLocalisation lookup), (settings.validateLocalisation lookup)))
        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        let infoService = (InfoService(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, varMap, loc, files, lookup.eventTargetLinksMap, lookup.valueTriggerMap, ruleValidationService, settings.changeScope, settings.defaultContext, settings.anyScope, settings.defaultLang, (settings.processLocalisation lookup), (settings.validateLocalisation lookup)))
        // log "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
        // game.RefreshValidationManager()
        debugChecks()
        ruleValidationService, infoService, completionService

    member __.LoadBaseConfig(rulesSettings) = loadBaseConfig rulesSettings
    member __.RefreshConfig() = refreshConfig()