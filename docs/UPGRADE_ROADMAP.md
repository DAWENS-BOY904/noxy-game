# Major upgrade: inspection and implementation status

## Existing project inspected

- Unity 6 project builds its lobby and Training scenes from editor code; UI is built in uGUI.
- The lobby already has a rotating placeholder character, selected-mode state, unavailable-mode messaging, atmospheric particles, animated lighting, and a drifting image backdrop.
- The only included character/weapon visuals are procedural placeholders. There are no imported character rigs, animation clips, music, or sound files.
- Training is an offline local prototype. The Node service is a development/social backend, not an authoritative shooter server. It cannot safely resolve hits, rounds, rewards, or competitive progression.
- Google/Apple provider credentials and a production Supabase project are not present. The Supabase and local mock work is documented separately in `AUTH_SETUP.md`.
- The attached images are references only; they are not imported as game assets. Production content must be original or properly licensed.

## Short implementation plan and status

| Stage | Existing implementation / next work | Status |
|---|---|---|
| 1. Animated lobby, sound, modes | Retain the animated stage and existing real mode-selection state; add a single settings-aware music/SFX owner, generated original UI cues, and scene-aware music fades. Import an original loop to enable lobby music. | Lobby animation and mode states exist; audio plumbing implemented, production music missing. |
| 2. Characters and clothing | Create original rigged character prefabs and animation mappings; add a data-driven selector and server-validated ownership. Keep the current try-on Vault and loadout flow. | Vault supports existing cosmetic slots; no alternate character models or production animations yet. |
| 3. Weapon customization/progression | Define attachment compatibility against actual meshes and weapon definitions; persist progression and validate all XP/stats/rewards server-side. | Weapon data and skins exist; attachments and weapon mastery are not implemented. |
| 4. Lone Wolf and Training | Keep Lone Wolf unavailable until server matchmaking and authoritative round rules/results exist. Continue local-only Training drills and label them clearly. | Training prototype has local targets and drill scoring; Lone Wolf and match results are unavailable. |
| 5. Missions, rewards, emotes | Extend existing mission/reward screens only with validated backend claim paths; add original emote assets and favorites when clips exist. | Existing local/dev systems and placeholder emotes only; production progression is pending. |
| 6. Multiplayer and release hardening | Build an authoritative dedicated server, platform-native sign-in and secure token storage; validate on Windows, Android and iOS, then profile and optimize on devices. | Not implemented or platform-tested. |

## Stage 1 audio details

- `LobbyAudioService` is a persistent singleton with one music source and one UI SFX source.
- `Master`, `Music`, `Sound effects`, and mute settings are applied to playback. UI button and mode selection tones are procedurally synthesized and original to this project.
- Music fades out when leaving `MainLobby` and resumes/fades in on return. Add an original or properly licensed clip as `Assets/_Project/Resources/Audio/LobbyMusic` to enable it.
- No background music, environmental sound, voice chat, or combat audio asset is included. No missing audio is represented as if it were playing.

## Verification

- Compile harness: passed with 0 warnings and 0 errors. This harness does not execute Unity scenes or Editor tooling.
- Core and client/backend integration tests: 156 passed.
- Node development backend tests: 28 passed.
- Unity Editor play mode, Android/iOS builds, visual layout, audio playback and platform device behavior remain unverified.
