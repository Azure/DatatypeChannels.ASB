#!/usr/bin/env -S dotnet fsi
#load "Pipeline.fsx"

match fsi.CommandLineArgs |> Array.skip 1 with
| [| "build"; artifact |] -> Pipeline.build (System.IO.Path.GetFullPath artifact)
| [| "test"; artifact |] -> Pipeline.test (System.IO.Path.GetFullPath artifact)
| [| "publish"; artifact |] -> Pipeline.publish (System.IO.Path.GetFullPath artifact)
| [| "check-trust" |] -> Pipeline.assertTrusted ()
| _ -> failwith "Usage: RunPipeline.fsx build|test|publish <artifact-directory> | check-trust"
