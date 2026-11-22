# Changelog

## 0.1.4

- The camera can follow, mount on or orbit missiles that mods fly on each machine without a network identity. It lets go when the missile explodes. `LocalTargets` in the config sets which kinds count.
- Exports wait for FarLands to finish drawing its distant land, so it shows in the video as it does while watching.
- With several camera keys selected, the replay speed buttons in the Tab menu set all of them at once.
- Exports at 2x or more render the camera straight at the video size. Unity's larger capture left out FarLands' distant trees, rocks and buildings, and blurred some 4K and 6K videos. With the game HUD on, the old capture is kept, with the game's motion blur and antialiasing off for the export.
- A small box in the corner shows the export's progress, the time left and that F10 cancels. It isn't in the video.
- Clicking a camera key to select it no longer nudges it: a key moves only after a short drag, and keeps the spot where it was grabbed.
- Dragging along the row the keys sit in selects every key in that stretch. Shift adds them to the selection.
- The Export section picks the video codec: H.264, H.265, AV1 or ProRes (a .mov for video editors), all from the FFmpeg export already uses. Auto, the default, uses H.264, or H.265 for videos larger or faster than H.264 players handle. 6K videos, and 4K above 60 fps, came out as H.264 that Windows and most players showed as corrupted.
- Screenshots at 2x or more render the camera straight at that size, so FarLands' distant objects are in them.
- *Hide the wind lines and the dust in the air* in Time and weather turns off the game's wind streaks and drifting specks.
- *Set the level of detail* overrides the game's level of detail from 1 to 7 during the replay, so far objects keep their full models further away. The game's own setting goes up to 5.
- The camera path's speed labels say that the whole path speed multiplies each key's speed, and what a key plays at.
- Changing the time of day or weather while paused shows its light on water and shiny surfaces at once. The game refreshes that reflection only as game time passes, so it waited for the replay to play.
- A camera path between two keys close together no longer swings out in a long loop when the keys around them are far away.

## 0.1.3

- Replays use the terrain of worldgen mods built on Worldhalla, such as Riverheim, instead of vanilla terrain. Recordings now keep the world's generator settings; older ones use the saved world of the same name and seed, if you have it. The terrain mod must be installed to watch such a replay.

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
