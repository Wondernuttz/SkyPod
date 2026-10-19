SkyPod 1.0.0 source distribution

SkyPod/ contains the original Windows Studio, shared application code,
native game plugin, MCM script, in-game web UI and build tools. Original
SkyPod software is GPL-3.0-only. See SkyPod/LICENSE.txt and SkyPod/NOTICE.txt.

third-party/ contains dependency source at the versions/revisions listed
below. Original third-party copyright and license notices are preserved.
Required build-configuration submodules are included at their expected paths.
Documentation-theme submodules are not required to build the shipped software.

The separately executed FFmpeg encoder remains LGPL-2.1-or-later. Its modified
source and build script are supplied separately in the Studio package at
Licenses/FFmpeg/Skypod-AudioEncoder-8.1.3-Source.tar.xz. No FFmpeg files have
been relicensed by this distribution.

Unmodified Microsoft .NET runtime/framework libraries are System Libraries;
their licenses and notices accompany the Studio runtime. Separately packaged
Reactive Extensions and System.IO.Abstractions sources are included below.

Build instructions: SkyPod/BUILD-SOURCE.txt. To modify a dependency, use its
included project/build instructions, build the matching NuGet package or
native library, and use that replacement while rebuilding SkyPod. The managed
package lock files record the exact restore versions used for this snapshot.

DEPENDENCY SOURCE LOCATIONS

DynamicData/9.4.31
third-party/DynamicData-8db480674d09
reactiveui/DynamicData @ 8db480674d0971c46e4ea872c74f3973e2496e2b

FluentResults/3.15.2
third-party/FluentResults-8b675de7e8b6
altmann/FluentResults @ 8b675de7e8b6e4c0921e95138c99e15fd52cde40

GameFinder.Common/4.9.0, GameFinder.RegistryUtils/4.9.0, GameFinder.StoreHandlers.GOG/4.9.0, GameFinder.StoreHandlers.Steam/4.9.0, GameFinder.StoreHandlers.Xbox/4.9.0, GameFinder.Wine/4.9.0
third-party/GameFinder-efa23bd6d02e
erri120/GameFinder @ efa23bd6d02ec07aa9fb1abe1025efafe3190d9f

ini-parser-netstandard/2.5.3
third-party/ini-parser-b1beea584b23
nano-byte/ini-parser @ b1beea584b23c65f7c9bcafd8ab9be62395f477a

K4os.Compression.LZ4/1.3.8, K4os.Compression.LZ4.Streams/1.3.8
third-party/K4os.Compression.LZ4-f5a25b7d72e2
MiloszKrajewski/K4os.Compression.LZ4 @ f5a25b7d72e2e41550fe20662597169ff11c3b60

K4os.Hash.xxHash/1.0.8
third-party/K4os.Hash.xxHash-c36ef54edbd7
MiloszKrajewski/K4os.Hash.xxHash @ c36ef54edbd778c082cab04df7e42cb72a22d887

Loqui/3.7.0
third-party/Loqui-da23e9867105
Noggog/Loqui @ da23e9867105c91e855edf791a35386ef038f063

Mutagen.Bethesda.Core/0.54.4, Mutagen.Bethesda.Kernel/0.54.4, Mutagen.Bethesda.Skyrim/0.54.4
third-party/Mutagen-0188012c607c
Mutagen-Modding/Mutagen @ 0188012c607ce8bb283d2704400d37737f089134

NexusMods.Paths/0.19.1
third-party/NexusMods.Paths-99b62efd5af8
Nexus-Mods/NexusMods.Paths @ 99b62efd5af88b83a0bc9eba8b3607afe802bbed

Noggog.CSharpExt/4.3.0
third-party/CSharpExt-dc1558f85e75
Noggog/CSharpExt @ dc1558f85e7595bdbae20a18a57905c3a07add9d

OneOf/3.0.271
third-party/OneOf-6e02dbe75d0f
mcintyre321/OneOf @ 6e02dbe75d0f20f198c640d4c04190a85c5ac9e6

Reloaded.Memory/9.4.2
third-party/Reloaded.Memory-6bab99d723bf
Reloaded-Project/Reloaded.Memory @ 6bab99d723bf6f783d24ed0d489db28ea5d9e075

SharpZipLib/1.4.2
third-party/SharpZipLib-33f64eb0f28c
icsharpcode/SharpZipLib @ 33f64eb0f28cdd2b084cb822fcc224c7c5aba553

StrongInject/1.4.4
third-party/stronginject-6f3077c68b1e
YairHalberstadt/stronginject @ 6f3077c68b1ebd9c56663456efb67ef90886942e

TestableIO.System.IO.Abstractions/22.1.1, TestableIO.System.IO.Abstractions.Wrappers/22.1.1, System.IO.Abstractions/22.1.1
third-party/System.IO.Abstractions-a091ac7f9655
TestableIO/System.IO.Abstractions @ a091ac7f9655f61aeaeb28baf452c1a96c546ba8

Testably.Abstractions.FileSystem.Interface/10.1.0
third-party/Testably.Abstractions-1253c6bd6309
Testably/Testably.Abstractions @ 1253c6bd6309cb15623fac09bd841e782bbca626

TransparentValueObjects.Abstractions/1.1.0
third-party/TransparentValueObjects-ff32127019b0
erri120/TransparentValueObjects @ ff32127019b0f53480f5e9c1867924b14138999c

ValveKeyValue/0.13.1.398
third-party/ValveKeyValue-c7d0dcce45a3
ValveResourceFormat/ValveKeyValue @ c7d0dcce45a32123e376c30c6fb9a016d6ebf294

System.Reactive/6.1.0
third-party/reactive-f4da16f15a3c
dotnet/reactive @ f4da16f15a3cde97f178396ea6e3489cc893651f

System.Interactive.Async/7.0.1, System.Linq.Async/7.0.1
third-party/reactive-96eed184a844
dotnet/reactive @ 96eed184a84471105d5d701a99e0b98629703d3f

NexusMods.Paths build configuration
third-party/NexusMods.Paths-99b62efd5af8/extern/meta
Nexus-Mods/NexusMods.App.Meta @ 9c4844439c53f8b8b9f64fb707c91b469238b15e

Reloaded.Memory build configuration
third-party/Reloaded.Memory-6bab99d723bf/src/Reloaded.Project.Configurations
Reloaded-Project/Reloaded.Project.Configurations @ 38db64b2a6c6952a66e1fe00cd344bc2f31c4b70

CommonLibSSE
third-party/CommonLibSSE-c41fe56f378c
Revision c41fe56f378c7f81f27324a41d56a17ccc8e024c; original MIT license.

directxmath 2025-04-03
third-party/native/directxmath
Build port: third-party/native/build-ports/directxmath

directxtk 2025-10-27
third-party/native/directxtk
Build port: third-party/native/build-ports/directxtk

fmt 12.1.0
third-party/native/fmt
Build port: third-party/native/build-ports/fmt

rapidcsv 8.90
third-party/native/rapidcsv
Build port: third-party/native/build-ports/rapidcsv

spdlog 1.16.0
third-party/native/spdlog
Build port: third-party/native/build-ports/spdlog
