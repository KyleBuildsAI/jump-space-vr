# Jump Space VR mod research

*As of 2026-10-22*

A genuine 6-DoF VR mod of Jump Space (Unity 6000.3.9f1 · IL2CPP · HDRP · DX12) is unproven-to-infeasible with today's tooling: code injection is *probably* possible but unconfirmed on this exact build, true in-engine stereo is effectively a blocker, and the only guaranteed VR outcome is head-tracked flatscreen cinema.

## Executive summary & verdict

Build the loader first and let three gates decide how far to go: injection is plausible, true stereo is not, and online use is tolerable only kept solo and client-local.

| Capability | Verdict | Why |
| --- | --- | --- |
| **(a) Inject & run code** (IL2CPP loader) | **Yellow** | A metadata-v39 toolchain exists (MelonLoader 0.7.3 / BepInEx 6 bleeding-edge + Cpp2IL #21), and doorstop injection is render-pipeline- and graphics-API-agnostic. But nothing is confirmed on this exact 6000.3.9f1 build, every component is pre-release/CI, and no IL2CPP + Unity-6 in-game inspector is confirmed to exist. |
| **(b) Render true in-engine stereo** (HDRP / DX12) | **Red** | HDRP VR needs Forward + single-pass-instanced stereo baked into the HDRPAsset at build time plus a shipped XR provider plugin — this game has neither. DX12 is the load-bearing blocker (every universal Unity VR injector is DX11-only), and the custom Keepsake native render plugins likely own the D3D12 present path. No public precedent on Unity 6 + HDRP + IL2CPP. |
| **(c) Use it online without a ban** | **Green (conditional)** | No EasyAntiCheat / BattlEye on disk and EOS base services do not scan memory, so injection is not hard-blocked. Residual risks are a manual ToS/EULA ban, Sentry crash telemetry leaking your DLL's name, and "solo" possibly still establishing an EOS session. Keep it client-local and offline. |

**Bottom line.** The realistic best outcome is a *head-tracked flatscreen theater* — the game's existing flat image on a giant virtual screen, head-look driving the camera. That is a weekend of work, engine-agnostic, low immersion, and has no stereo. A *faux-3D depth-reprojection* layer (ReShade SuperDepth3D) is a **maybe** that could add screen-depth over a few weeks *if* ReShade can resolve a clean depth buffer through HDRP's deferred pipeline with DLSS and TAA disabled. *Genuine 6-DoF in-engine stereo* — the actual VR goal — has **no demonstrated precedent** on this stack and should be treated as research-grade: months of bleeding-edge reverse-engineering with a real chance of never reaching the headset. Do not commit VR scope until the loader and stereo gates in the roadmap below pass.

## Verified target profile

These facts come from direct inspection of the installed files and are treated as ground truth; web sources that call the engine "unconfirmed" are wrong.

| Attribute | Value |
| --- | --- |
| Game | Jump Space (formerly Jump Ship), Steam appid 1757300, developer Keepsake Games |
| Type | Early-Access co-op PvE spaceship FPS, 1–4 players (roles: pilot / gunner / mechanic / on-foot shooter), released 2025-09-19 |
| Engine | **Unity 6000.3.9f1** (Unity 6.3) — 2026 bleeding edge |
| Scripting backend | **IL2CPP** (GameAssembly.dll ~394 MB, il2cpp.usym present, global-metadata.dat ~34 MB). **Not Mono** |
| Render pipeline | **HDRP** (metadata has HDRenderPipelineAsset + HDAdditionalCameraData; globalgamemanagers shows ScriptableRenderPipeline). **Not URP, not built-in** |
| Graphics API | **DirectX 12** (D3D12 folder), NVIDIA **DLSS** (nvngx_dlss.dll), Unity **Burst** jobs (lib_burst_generated.dll) |
| Online stack | **Epic Online Services** (EOSSDK-Win64-Shipping.dll, KeepsakeEOSMemoryIntegration.dll) + **Steamworks** (steam_api64.dll, Steamworks.NET) |
| Netcode | Markers for **Unity Netcode** (41 metadata hits) + **Mirror** (12 hits) |
| Anti-cheat | Only 2 "AntiCheat" string hits; **no EasyAntiCheat / BattlEye files on disk** — EAC is not integrated or enforced |
| XR | **No XR plugin** in Data/Plugins (no built-in VR). Metadata references UnityEngine.XR (25 hits) + OpenVR (4 hits) = engine-module *wrappers* only, no backend |
| Other native | Custom Keepsake plugins (NativeRenderingControl.dll, KeepsakeNativeRenderingUtils.dll, KeepsakePhysicsUtils.dll); Wwise audio; Sentry + crashpad crash telemetry; Dear ImGui debug overlay. Clean install (no BepInEx / MelonLoader yet) |

## Why this is a hard target

No single property is fatal on its own, but they stack: this is close to the worst combination a flat-to-VR modder can face. A successful universal VR mod usually needs an old-ish Unity version, Mono, built-in/URP rendering, and DX11 — Jump Space is the opposite on every axis.

| Layer | What it adds to the difficulty |
| --- | --- |
| Unity 6.3 (6000.3.9f1) | A 2026 release, newer than anything any VR injector or mod loader has been validated against; expect undocumented breakage. |
| IL2CPP | Fully AOT-compiled C++, no JIT or reflection. Modding needs a Cpp2IL dump of GameAssembly.dll + global-metadata.dat and Il2CppInterop-generated proxies; the v39-capable tooling exists only on pre-release/CI channels. |
| HDRP | VR is supported *only* via Forward + single-pass-instanced stereo, baked into the HDRPAsset and Project Settings at build time. HDRP's custom render loop (HDCamera / XRPass) ignores the legacy `camera.stereoEnabled` flags that VRGIN-style mods drive, and shipped shaders are compiled mono/non-SPI (they would render one eye only). |
| DirectX 12 | The worst-supported API for Unity XR — VR has been effectively DX11-only for years. Every universal injector (UUVR, UnityVRMod, VRGIN) is DX11-only, and Unity itself still ships no HDRP DX12 stereo shader example as of Sept 2025. |
| No shipped XR provider | There is no XRDisplaySubsystem to start; the entire native + managed XR plugin stack must be injected and ABI-matched to this exact engine build, then HDRP forced to re-init and pick it up. |
| Custom native render plugins | NativeRenderingControl.dll / KeepsakeNativeRenderingUtils.dll almost certainly hook the D3D12 device/present on the render thread; an injected XR submission layer can collide (device-removed, D3D12 validation errors). |
| Online (EOS + Netcode/Mirror) | Any VR hook must stay client-local or risk desync/kick in co-op; a VR mod is realistically a single-player/local-only proposition. |
| Multiple cameras | Pilot / gunner / mechanic / on-foot views break the single-main-camera assumption VRGIN-style mods depend on, so each view needs custom handling even if stereo worked. |

## Feasibility by dimension

| Dimension | Feasibility | Key finding & named tools |
| --- | --- | --- |
| IL2CPP mod-loader toolchain (Unity 6.3) | **Medium** (unconfirmed on this build) | Target metadata is v39. **MelonLoader 0.7.3** bundles **Cpp2IL 2022.1.0-pre-release.21** (adds v39, "up to 6000.5.0 alpha") + **Il2CppInterop 1.5.1-ci.845** with explicit Unity 6000+ fixes — the most turnkey route. **BepInEx 6 bleeding-edge be.755+** is the alternative. Doorstop injection is pipeline/API-agnostic; no EAC blocks it. |
| Universal Unity VR injectors (UUVR / UnityVRMod) | **Blocker** | **NewUnityModder/UnityVRMod** is explicitly **DirectX 11 only** — a hard wall against DX12. **Raicuparta/uuvr** (native-XR re-injection, installed via Rai Pal) is the right concept but has no HDRP/DX12/Unity-6 support and a fragile IL2CPP path. Both depend on BepInEx IL2CPP tooling. |
| HDRP stereo VR rendering | **Blocker** | HDRP VR = Forward + single-pass baked at build time; shipped HDRPAsset is flat-mono; DX12 + custom native plugins compound it. No public HDRP + IL2CPP + Unity-6 stereo precedent exists. Realistic ceiling is pseudo-VR, not native stereo. |
| Runtime native-XR enablement (Unity 6) | **Low** | XR display/input subsystems are *native* plugins and cannot be registered from C#. You must inject an ABI-matched **UnityOpenXR** provider for 6.3 (may not exist), then build XRGeneralSettings / XRManagerSettings / an XRLoader in code and `InitializeLoaderSync` + `StartSubsystems`. OpenXR is correct, not legacy OpenVR. |
| 6-DoF input, motion controllers & VR UI | **Low** | Loader gate first, then the HDRP stereo wall. Motion-controlled hands/weapons = bespoke per-role reverse engineering (only il2cpp.usym, no full symbols). HUD is screen-space uGUI needing world-space reprojection; the Dear ImGui overlay will not render in stereo. Favorable: no kernel anti-cheat. |
| Online / EOS / anti-cheat / ban-risk / ToS | **Medium** (low ban-risk, conditional) | EOS base ≠ anti-cheat; the absent EAC footprint (no start_protected_game.exe, no EasyAntiCheat folder) means injection is not hard-blocked and host-authority co-op tolerates camera-only mods. Risks: manual ToS ban, Sentry telemetry, solo-may-still-hit-EOS. |
| Alternative approaches & fallbacks | **Medium** (only the lowest tier is guaranteed) | Only head-tracked 2D cinema is certain. ReShade depth-3D is a maybe; geo-11 is effectively unavailable; Luke Ross AER is not a droppable tool; vorpX is a DX12 non-starter; OpenXR-Toolkit is a category error. None deliver true 6-DoF. |

**One contradiction to resolve first.** Several secondary sources (and three of the research passes) cite the IL2CPP metadata ceiling as v23–29 and quote a "We support 23-29, got 31" crash as proof Unity 6 is unsupported. That crash is from **old Unity 2022.3-era** builds; **Unity 6000.x emits metadata version 39 (0x27)**, which current Cpp2IL/MelonLoader channels do cover. Do not conflate them — but confirm it directly by reading byte offset 4 of this install's global-metadata.dat before trusting any toolchain claim.

## Recommended approach — phased roadmap

Do not write any VR code until the loader and stereo gates pass. Each phase ends in a GO/NO-GO gate; a failed gate drops you to a fallback rather than sinking more effort. Run the whole thing on a copy, solo and offline. (The doc renders this as a decision-flow diagram; the gate logic is below.)

| Gate | Test | GO → | NO-GO → |
| --- | --- | --- | --- |
| A | A loader attaches **and** metadata = v39 | Phase 1 | Stop — no loader means no mod of any kind |
| 1 | A plugin runs **and** the HDRP camera + transforms are resolvable | Phase 2 | Head-tracked cinema |
| 2 | XR display subsystem initializes **and** head-pose moves the camera | Phase 3 | Head-tracked cinema |
| 3 | Correct per-eye stereo reaches the HMD without a native-plugin crash | Phase 4 | ReShade faux-3D or cinema |

**Phase 0 — Measure & smoke-test (no VR scope yet).** Read byte offset 4 of `global-metadata.dat` (expect 0x27 = v39). Install **MelonLoader 0.7.3** (and, separately, **BepInEx 6 bleeding-edge be.755+**) with a do-nothing plugin on a *copy* of the game. Run **Cpp2IL pre-release.21** against GameAssembly.dll. Launch with `-force-d3d11`. Inspect the HDRPAsset (**AssetRipper**) and probe NativeRenderingControl.dll.

**Phase 1 — Loader foundation.** Pin the known-good loader build. Generate **Il2CppInterop** proxy assemblies from the Cpp2IL dump (**UnityDoorstop** / Apprentice9999 CoreCLR Doorstop handle injection). Stand up an in-game inspector — no confirmed IL2CPP + Unity-6 UnityExplorer exists, so expect to patch the yukieiji fork or fall back to static dumps + **dnSpy/ILSpy** (try leveraging il2cpp.usym).

**Phase 2 — XR bring-up (solo/offline).** Inject an ABI-matched **UnityOpenXR** provider + XR Plugin Management for 6.3; build `XRGeneralSettings` / `XRManagerSettings` / an `XRLoader` in code and call `InitializeLoaderSync` + `StartSubsystems`. Drive the HDRP camera with a `TrackedPoseDriver` for head tracking.

**Phase 3 — Stereo to HMD (the wall).** Force HDRP's `XRSystem` to re-init and pick up the subsystem; flip the HDRPAsset's single-pass / Forward path at runtime (an unsupported operation); handle the multiple cameras; patch shader variants for single-pass-instanced (likely impossible without source). This gate has no public precedent and is the most likely total-failure point.

**Phase 4 — 6-DoF input & VR UI (only if Gate 3 passes).** Map XR controller pose to on-foot aim / ship controls per role (hook the IL2CPP aim/look code or feed a virtual gamepad); reproject the screen-space uGUI HUD onto world-space quads. Keep every change client-local — camera and local input only, never networked movement — and treat it as single-player / local-only.

## Fallbacks

If true in-engine stereo fails (expected), these are the alternatives, best-first. Only Rank 1 is guaranteed; none deliver genuine room-scale 6-DoF — that remains exclusive to the blocked native-stereo path.

| Rank | Approach | Tools | Works here? | Honest quality ceiling |
| --- | --- | --- | --- | --- |
| 1 | Head-tracked 2D cinema | SteamVR Theater Screen, Virtual Desktop, Bigscreen, or FlatVR (Steam appid 4899200) + opentrack | **Yes — guaranteed.** Mirrors the flat framebuffer, no injection, engine/API/anti-cheat agnostic | Flat 2D screen in the HMD; optional faux-3D add-on. No stereo, no 6-DoF. Weekend of setup |
| 2 | ReShade depth-buffer stereo | SuperDepth3D / SuperDepth3D_VR (Depth3D) + FlatVR depth-provider output | **Maybe.** ReShade hooks DX12, but HDRP deferred + DLSS/TAA corrupt the depth buffer; must disable upscaling, and may still fail | Faux-3D depth warp with occlusion halos on a virtual screen; not true parallax or 6-DoF |
| 3 | geo-11 geometry-3D | geo-11 (built on 3Dmigoto, distributed via HelixMod) | **Effectively no.** Needs a hand-authored per-title shader fix on a deferred HDRP pipeline; DX12 path experimental; none exists | True per-pixel geometry stereo *if* a fix existed; authoring one rivals the native-mod effort. Still screen-space |
| 4 | Luke Ross AER | "REAL" mods (bespoke, Patreon per-title) | **No.** Each mod is hand-built by Ross for titles he personally reverse-engineers; none for this game, and replicating it = the hard native problem | Would be the best faux-6-DoF (alternate-eye + reprojection) if it existed |
| 5 | vorpX | vorpX | **No — hard non-starter.** Hooks DX9/10/11 only; no DX12 hook, so it cannot attach at all | n/a |
| 6 | OpenXR-Toolkit / "DX12 injection layers" | OpenXR-Toolkit (discontinued 2024) | **No — category error.** Post-processes games that are *already* OpenXR VR apps; cannot add VR to a flat game. No generic DX12→VR injector exists (UEVR is Unreal-only) | n/a |

Note: the injector fallbacks (Ranks 2–3) still touch a live EOS + Steamworks + Netcode/Mirror session, so carry a non-zero flag risk even with EAC absent — test offline/solo first.

## Risks

- **Tooling immaturity (Unity 6 bleeding edge).** Everything load-bearing is pre-release/CI: Cpp2IL has no stable tag, BepInEx 6 IL2CPP is bleeding-edge-only, and no component is confirmed against 6000.3.9f1. These builds change weekly and a specific build can break mods an adjacent build loads fine — pin a known-good build and expect churn.
- **No in-game inspector for IL2CPP + Unity 6.** UnityExplorer upstream is archived; the maintained yukieiji fork's Unity-6 IL2CPP status is unverified; the dedicated 6.3 fork is Mono-only. Runtime camera/transform/pipeline inspection — essential for bolting on stereo — may have to be hand-ported or replaced by static Cpp2IL dumps + dnSpy/ILSpy.
- **HDRP + VR performance.** HDRP VR forces Forward rendering (disables deferred) and discourages volumetrics; VR roughly doubles render cost; DLSS/TAA interplay is unresolved. This is already a heavy pipeline before any VR overhead.
- **Online / ban / ToS / telemetry.** No EAC on disk and EOS base services do not scan, so injection is not hard-blocked. But: (1) client modification violates the Steam Subscriber Agreement and a typical Keepsake EULA — a *manual* ban is possible, and more likely if you mod others' sessions, distribute the mod, or draw developer attention; (2) **Sentry + crashpad** transmits your injected DLL's name/path to Keepsake on any crash (likely on a bleeding-edge stack) — the main way a dev would notice; (3) the game has no shipped offline mode yet, so even "solo" may establish an EOS session; (4) a future Early-Access patch could ship start_protected_game.exe + an EasyAntiCheat folder and instantly block injection.
- **Early Access churn.** Frequent patches rev GameAssembly.dll and the metadata, repeatedly breaking the loader and any offset/hook-based VR code.
- **Native-plugin conflict.** The Keepsake D3D12 render plugins may collide catastrophically (device-removed, validation errors) with any injected XR submission layer or even a ReShade present hook.

## Open questions the user must test

Every load-bearing unknown is still open; none was resolved from the desk. Run these on the installed game, in order — the first two decide whether any VR work is possible at all.

1. **Read byte offset 4 of global-metadata.dat** and record the version (expect 0x27 = 39). Resolves the v39-vs-v31 contradiction and decides whether Cpp2IL pre-release.21 suffices. Do this first.
2. **On a COPY, smoke-test the loaders.** Install current bleeding-edge BepInEx 6 IL2CPP and, separately, the latest MelonLoader, each with a trivial do-nothing plugin. Confirm whether either chainloads into 6000.3.9f1 and logs. If neither attaches, no VR work is possible and everything downstream is moot.
3. **Run Cpp2IL (pre-release) against the actual GameAssembly.dll (~394 MB) + global-metadata.dat.** Check for a clean dump vs stub DLLs missing `[Address]` attributes, given Burst and the Keepsake native plugins. Also test whether il2cpp.usym can improve dump/RE accuracy.
4. **Launch with `-force-d3d11`** and observe run / render / crash. Gates every DX11-only injector route (and vorpX-class approaches).
5. **Inspect the shipped HDRPAsset** (AssetRipper / bundle inspection): Forward vs Deferred, and whether any XR / single-pass-instanced variant is compiled in. If strictly mono/Deferred, native stereo is off the table without rebuilding shader variants.
6. **Probe NativeRenderingControl.dll / KeepsakeNativeRenderingUtils.dll** (API Monitor, import table, `IUnityGraphics` / `CommandBuffer.IssuePluginEvent` usage) to establish whether they own the D3D12 present/swapchain.
7. **Test ReShade against the DX12 swapchain** with DLSS and TAA disabled: can it resolve a stable, correct depth buffer? Decides whether the Rank-2 faux-3D fallback is "maybe" or "no."
8. **Packet-capture a solo/single-player session** to confirm whether it disconnects from EOS or still establishes a network session — determines whether "test offline first" is genuinely off-network.
9. **After every Early-Access patch, re-check the game root** for newly shipped EAC enforcement files (start_protected_game.exe, EasyAntiCheat_EOS_Setup.exe, an EasyAntiCheat/Settings.json folder).

## Sources

**Game:** [Jump Space wiki](https://jumpspace.wiki.gg/wiki/Jump_Space) · [PCGamesN — first update](https://www.pcgamesn.com/jump-space/first-update)

**Mod loaders & IL2CPP tooling:** [Cpp2IL releases](https://github.com/SamboyCoding/Cpp2IL/releases) · [knah/Cpp2IL](https://github.com/knah/Cpp2IL) · [MelonLoader releases](https://github.com/LavaGang/MelonLoader/releases) · [MelonLoader v0.7.2](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.2) · [MelonLoader repo](https://github.com/LavaGang/MelonLoader) · [BepInEx releases](https://github.com/BepInEx/BepInEx/releases) · [BepInEx repo](https://github.com/BepInEx/BepInEx) · [BepInEx IL2CPP install docs](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html) · [BepInEx bleeding-edge builds](https://builds.bepinex.dev/projects/bepinex_be) · [BepInExPack_IL2CPP 6.0.755](https://thunderstore.io/c/we-love-katamari-reroll-royal-reverie/p/BepInEx/BepInExPack_IL2CPP/v/6.0.755/) · [BepInExPack_IL2CPP](https://thunderstore.io/c/ironstrike/p/BepInEx/BepInExPack_IL2CPP/) · [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) · [Il2CppInterop releases](https://github.com/BepInEx/Il2CppInterop/releases) · [Il2CppAssemblyUnhollower](https://github.com/BepInEx/Il2CppAssemblyUnhollower) · [Il2CppInterop.Runtime (NuGet)](https://www.nuget.org/packages/Il2CppInterop.Runtime) · [Il2CppInterop.CLI (NuGet)](https://www.nuget.org/packages/il2cppinterop.cli) · [UnityDoorstop (TechInterMezzo)](https://github.com/TechInterMezzo/UnityDoorstop) · [UnityDoorstop CoreCLR (Apprentice9999)](https://github.com/Apprentice9999/UnityDoorstop) · [Il2CppDumper #892 (Unity 6000.3 / v39)](https://github.com/Perfare/Il2CppDumper/issues/892) · [UnityExplorer releases (archived)](https://github.com/sinai-dev/UnityExplorer/releases) · [Yukieji UnityExplorer fork](https://thunderstore.io/c/lost-skies/p/Azim/Yukieji_UnityExplorer/) · [HowToFish UnityExplorer 4.14.2 (Mono-only)](https://thunderstore.io/c/how-to-fish/p/HowToFish_Modding/UnityExplorer/v/4.14.2/)

**VR injectors & frameworks:** [Raicuparta/uuvr](https://github.com/Raicuparta/uuvr) · [uuvr issues](https://github.com/Raicuparta/uuvr/issues) · [uuvr #35](https://github.com/raicuparta/uuvr/issues/35) · [uuvr #56](https://github.com/raicuparta/uuvr/issues/56) · [NewUnityModder/UnityVRMod](https://github.com/NewUnityModder/UnityVRMod) · [UnityVRMod releases](https://github.com/NewUnityModder/UnityVRMod/releases) · [kidonaru/UnityVRMod](https://github.com/kidonaru/UnityVRMod) · [Eusth/VRGIN](https://github.com/Eusth/VRGIN) · [Rai Pal (info)](https://raicuparta.com/rai-pal) · [Rai Pal (app)](https://pal.raicuparta.com) · [roadtovr — Human Fall Flat VR](https://www.roadtovr.com/human-fall-flat-vr-mod-raicuparta-quest-steam-psvr/) · [Pimax — flat-to-VR mods](https://pimax.com/blogs/highlights/flat-to-vr-mods-are-creating-a-new-generation-of-vr-games) · [UEVR](https://lr.psf.lt/r/uevr) · [Nexus — AI Limit](https://www.nexusmods.com/ailimit/mods/28?tab=posts) · [Nexus — IronNest](https://www.nexusmods.com/ironnest/mods/2)

**HDRP & Unity XR docs:** [Unity — photorealistic VR with HDRP](https://unity.com/blog/technology/creating-immersive-photorealistic-vr-experiences-with-the-high-definition-render) · [VR in HDRP (notes)](https://github.com/shenzhou05/SRP-Chinese-Translation/blob/master/Pages/HDRP/VR-in-HDRP.md) · [Single-pass stereo (manual)](https://docs.unity3d.com/Manual/SinglePassStereoRendering.html) · [Single-pass stereo — Unity 6000.3](https://docs.unity.com/en-us/engine/6000.3/manual/xr/graphics/stereo-rendering/single-pass-stereo-rendering) · [Single-pass stereo — Unity 6000.6](https://docs.unity.com/en-us/engine/6000.6/manual/xr/graphics/stereo-rendering/single-pass-stereo-rendering) · [Direct3D12 HDRP VR thread](https://discussions.unity.com/t/direct3d12-hdrp-vr-support/774588) · [OpenVR won't launch on D3D12](https://issuetracker.unity3d.com/issues/openvr-doesnt-launch-into-vr-if-d3d12-is-the-primary-graphics-api) · [XR foveated + HDRP](https://discussions.unity.com/t/xr-foveated-rendering-does-not-work-with-hdrp/1664857) · [Unity XR updates, Sept 2025](https://discussions.unity.com/t/unity-xr-updates-september-2025/1684847) · [SPI custom HDRP shader](https://forum.unity.com/threads/how-to-add-support-for-single-pass-instanced-stereo-rendering-mode-in-my-custom-hdrp-lit-shader.886669/) · [Custom SPI matrices in HDRP](https://discussions.unity.com/t/custom-single-pass-stereo-matrices-in-hdrp-how/1624042) · [SPI shows only skybox](https://issuetracker.unity3d.com/issues/single-pass-stereo-rendering-shows-only-skybox-when-entering-play-mode) · [XR provider implementation](https://docs.unity3d.com/Packages/com.unity.xr.arfoundation@6.0/manual/implement-a-provider.html) · [XR Management changelog](https://docs.unity3d.com/Packages/com.unity.xr.management@4.3/changelog/CHANGELOG.html) · [XR Management end-user](https://docs.unity.cn/Packages/com.unity.xr.management@3.2/manual/EndUser.html) · [XR module manual](https://docs.unity3d.com/Manual/com.unity.modules.xr.html) · [Manual OpenXR load](https://discussions.unity.com/t/manual-openxr-load-with-unity-input-system-not-working/832884) · [XRGeneralSettings null](https://forum.unity.com/threads/xrgeneralsettings-instance-is-null-in-editor-play-mode-openvrloader-not-initializing.1094446/) · [Detect headset & init XR](https://discussions.unity.com/t/how-to-detect-if-headset-is-available-and-initialize-xr-only-if-true/799147)

**Online, anti-cheat & telemetry:** [EOS Anti-Cheat docs](https://dev.epicgames.com/docs/game-services/anti-cheat) · [Using EOS Anti-Cheat](https://dev.epicgames.com/docs/game-services/anti-cheat/using-anti-cheat) · ["EAC is not installed"](https://www.easy.ac/support/articles/error-eac-is-not-installed) · [Neowin — EOS anti-cheat on ARM](https://www.neowin.net/news/epic-unlocks-more-multiplayer-games-for-windows-on-arm-with-anti-cheat-support/) · [Netcode for GameObjects docs](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.7/manual/toc.html) · [Connection approval](https://docs-multiplayer.unity3d.com/netcode/current/basics/connection-approval/) · [Sentry crash reporter](https://docs.sentry.io/platforms/unreal/configuration/crash-reporter/sentry-crash-reporter/) · [Sentry — minidumps on SteamOS](https://blog.sentry.io/not-so-mini-dumps-how-we-found-missing-crashes-on-steamos) · [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) · [Steam community discussion](https://steamcommunity.com/app/1501750/discussions/0/591770005685724262)

**Fallbacks & stereo tools:** [vorpX supported games](https://www.vorpx.com/supported-games/) · [vorpX features](https://www.vorpx.com/features/) · [Depth3D (SuperDepth3D)](https://github.com/BlueSkyDefender/Depth3D) · [ReShade depth-3D shader](https://reshade.me/forum/shader-presentation/2128-3d-depth-map-based-stereoscopic-shader) · [geo-11 / 3Dmigoto](https://github.com/bo3b/3Dmigoto) · [HelixMod blog](http://helixmod.blogspot.com/) · [Luke Ross — vrgamingmods](https://www.vrgamingmods.com/) · [Luke Ross — Patreon](https://www.patreon.com/realvr) · [wccftech — Spider-Man VR mod](https://wccftech.com/marvels-spider-man-vr-mod-by-luke-ross-is-now-available-to-patreon-subscribers/) · [OpenXR-Toolkit (discontinued)](https://github.com/mbucchia/OpenXR-Toolkit) · [OpenXR-Toolkit releases](https://github.com/mbucchia/OpenXR-Toolkit/releases) · [SteamVR Theater Screen](https://www.roadtovr.com/steamvr-theater-screen-flatscreen-vr-games/) · [FlatVR (Steam)](https://store.steampowered.com/app/4899200) · [opentrack](https://github.com/opentrack/opentrack) · [Virtual Desktop](https://www.vrdesktop.net/)

---

## Appendix A — Verified on-disk evidence (provenance)

Everything in the "Verified target profile" section was established by directly inspecting the installed game files at `G:\SteamLibrary\steamapps\common\JumpSpace`, not from web sources (web coverage does not even confirm the engine). The checks:

| What | How it was determined | Result |
|---|---|---|
| Install path | `appmanifest_1757300.acf` in `G:\SteamLibrary\steamapps` | `installdir = JumpSpace`, name "Jump Space" |
| Scripting backend | Presence of `GameAssembly.dll` (~394 MB) + `il2cpp.usym` + `Jump Space_Data/il2cpp_data/Metadata/global-metadata.dat` (~34 MB) | **IL2CPP** (not Mono — no `Managed/` folder) |
| Unity version | `UnityPlayer.dll` file version + `ProductVersion` | **6000.3.9f1** (Unity 6.3) |
| Render pipeline | class-name strings in `globalgamemanagers` + `global-metadata.dat`: `ScriptableRenderPipeline`, `HDRenderPipelineAsset` (5), `HDAdditionalCameraData` (4); no URP markers | **HDRP** |
| Graphics API | `D3D12/` folder present; `nvngx_dlss.dll` (DLSS); `lib_burst_generated.dll` (Burst) | **DX12** |
| Online stack | `Data/Plugins`: `EOSSDK-Win64-Shipping.dll`, `KeepsakeEOSMemoryIntegration.dll`, `steam_api64.dll`, `Steamworks.NET.txt`; metadata: Netcode (41), Mirror (12), EpicOnlineServices (7), SteamManager (10) | **EOS + Steamworks**, Unity Netcode |
| Anti-cheat | No `EasyAntiCheat*`, `BattlEye*`, `BEClient*`, `start_protected_game.exe` anywhere; only 2 `AntiCheat` string hits in metadata | **No EAC enforced on disk** |
| XR present? | No XR/OpenXR/OpenVR plugin DLL in `Data/Plugins`; metadata references `UnityEngine.XR` (25), `OpenVR` (4) | Game ships **no VR**; engine XR module types present |
| Custom native code | `NativeRenderingControl.dll`, `KeepsakeNativeRenderingUtils.dll`, `KeepsakePhysicsUtils.dll` | Custom rendering/physics — may interfere with injected stereo |
| Telemetry | `sentry.dll`, `crashpad_handler.exe` | Crashes (incl. mod crashes) may be reported to the dev |

Re-run anytime: inspect `UnityPlayer.dll` version, list `Jump Space_Data/Plugins`, and grep `Jump Space_Data/il2cpp_data/Metadata/global-metadata.dat` for the markers above.

---

## Appendix B — Transferable lessons from the Freedive / UEVR project

This research session was forked from **Freedive**, a Windows launcher that makes flat **Unreal Engine** games VR by orchestrating praydog's UEVR. Jump Space is **Unity**, so UEVR itself does not apply — but several lessons carry over directly:

- **Orchestrate, don't reimplement.** Freedive never reimplements VR rendering or injection; it drives the existing, community-trusted injector (UEVR) and writes per-game config. The Unity equivalent is to stand on **BepInEx 6 (IL2CPP) + a VR framework (UUVR / UnityVRMod)** rather than writing a stereo renderer from scratch. Reuse > rebuild.
- **The injection mechanism is the biggest unknown — spike it first.** Freedive's hardest risk (R1) was *how to drive the injector non-interactively*; the answer (`--attach=<exe>`) was only settled by inspecting the real binary and running it. Mirror that here: **Gate 1 is "can a loader even attach and run code against Unity 6.3 IL2CPP"** — prove it with a trivial BepInEx/UnityExplorer load before building anything else. Don't design the whole mod before that spike passes.
- **Anti-cheat / online is a hard gate, not a nuance.** Freedive *blocks* injection into any anti-cheat title and warns that any online game risks bans even when nothing is detected. Jump Space has **no EAC on disk**, but it is **online co-op over EOS** — so the same discipline applies: develop and test **solo/offline**, never inject into another player's session, and treat EOS/Steam ToS as real. "No EAC file" ≠ "safe to mod online."
- **Determinism + validation at the boundary.** Freedive's model emits validated JSON and a pure mapper writes the config, so a bad input can't corrupt the game config. For a Unity VR mod, keep the same posture: validated, versioned config for offsets/world-scale/comfort, written deterministically — not hand-edited magic numbers.
- **Unity was explicitly the "Phase 2 / UUVR" path in Freedive.** Freedive already detects Unity games (via `UnityPlayer.dll` + IL2CPP vs Mono) and labels them "Not supported yet — UUVR backend planned." Jump Space is precisely that case: a Unity IL2CPP title whose VR path is **UUVR-class injection**, not UEVR. This report is the deep-dive that the Freedive "Phase 2" note deferred.
- **Verify by running, document the human-only steps.** Freedive's TESTING.md separates what was machine-verified from what needs a headset + a person. Do the same here: the on-disk profile is verified; whether stereo actually reaches the HMD is a **headset-in, hands-on** gate that only testing on this machine can settle.
