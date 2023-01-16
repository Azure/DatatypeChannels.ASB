module DatatypeChannels.ASB.Program

open Expecto

[<EntryPoint>]
let main argv = 
    Tests.runTestsInAssemblyWithCLIArgs [] argv
