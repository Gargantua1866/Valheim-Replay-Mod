# Changelog

## 0.1.2

- Ctrl+F8 puts a mark on the timeline while recording or buffering. Change it with `MarkShortcut` in the config.
- Leaving a replay with saving keeps the playhead, camera position and camera settings, and the replay opens there next time.
- Eight camera presets instead of four, and each can be renamed.
- Lens dirt in the bloom is off by default. Turn it on in the Picture section.
- Recording costs less: the file is compressed on a background thread, characters standing still write nothing at the physics rate, and the scan for changed objects does less work.
- Recordings now hold the game's network clock, and replays run on it. Mods that start an animation from a time stored on an object, such as tricks on vehicles, now play it in replays as it was recorded. Recordings made before this version don't have the clock.

## 0.1.0

First public release.

- Record in single player or multiplayer, as host or client, by hand or with the Replay Buffer.
- Watch a recording in a separate replay world with a free camera, camera paths, bookmarks, and follow, mount, orbit and look-at targets.
- Recordings hold players, creatures, ships, vehicles and projectiles, scenery around you, effects, your camera, time of day, weather, wind and waves. They also hold your hotbar, food, status effects and explored map.
- Moving objects are recorded at a high rate, so fast vehicles and slow motion play back smoothly.
- Trim a section into a new replay, or export the camera path, a selected section or the whole replay as MP4 with sound.
- After a far teleport the replay pauses while the new area loads. The terrain of the area you left stays loaded, so going back is quicker.
