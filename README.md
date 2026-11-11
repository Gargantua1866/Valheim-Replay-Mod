# Replay Mod

Replay Mod records everything that happens around you in Valheim. Open the recording afterward in a separate replay world, fly a free camera through it, build camera paths and export the result as an MP4.

Showcase video
[![Replay Mod showcase video](https://img.youtube.com/vi/tQ8CQNI6xOA/maxresdefault.jpg)](https://youtu.be/tQ8CQNI6xOA)

## Features

- Record in single player or multiplayer, as host or as client, by hand or with a Replay Buffer that saves the last few minutes on a key press.
- Fly a free camera with FOV and roll, or follow, mount, orbit or look at any character, vehicle or projectile.
- Build camera paths from keys, with easing, per-key FOV, roll, replay speed, time of day and weather.
- Change the replay's speed, time of day, weather, wind, depth of field and picture settings.
- Trim a replay into a shorter one.
- Export video at a fixed frame rate, with sound, and take screenshots without any overlay.

## Installation

Install Replay Mod with a mod manager such as Gale, which also installs BepInEx. To install manually, put `ReplayMod.dll` in `BepInEx/plugins/ReplayMod/`.

Video export requires FFmpeg, which Replay Mod doesn't include. See [Export](#export).

## Recording

In game, press **Ctrl+F10** to start/stop recording. Press **Ctrl+F8** while recording or buffering to put a mark on the timeline.

**Replay Buffer:** Enable it in the Replays tab or with **Ctrl+Alt+F10** in game. **Ctrl+Shift+F10** then saves the last few minutes while you play. Change the length in the Replays tab or the config. The buffer records all the time, so it uses as many resources as a normal recording does.

A recording holds:

- players, creatures, ships and projectiles the game has loaded around you
- scenery (trees, rocks, buildings, terrain edits) in the 3x3 zones around you
- attacks and effects
- your hotbar, food, status effects and explored map, shown in the replay when the game HUD is on
- your camera, the time of day, the weather, the wind and the waves

Recordings go to `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\ReplayMod\replays`. Each `.replay` file has a `.json` next to it with size and timing statistics.

## Watching a replay

1. On the main menu, pick a character and press Start.
2. Open the **Replays** tab, pick a replay and press **Play**. The panel beside the list shows the replay's thumbnail and lets you rename or copy it.
3. The replay opens paused, in a temporary world built from the recorded world's seed. Everything still loads while paused. Nothing is saved to that world or to your character.
4. To leave, use **Escape → Logout** or **Exit replay** in the Tab menu. The Tab menu can also leave without saving, which puts camera keys and bookmarks back to how they were when the replay opened.

### Keys

These keys work inside a replay and can't be changed.

| Key | Action |
|---|---|
| Mouse | Look |
| W A S D | Move |
| E / Q | Up / down |
| Shift / Ctrl | Fast / slow |
| Wheel | Move speed |
| Alt + wheel, R / F | FOV (R zooms in, F zooms out) |
| Z / C | Roll |
| X | Reset roll and turn an inverted view upright |
| Tab | Free the cursor and open the menu. Click or drag the timeline at the bottom to seek |
| Space | Pause / play |
| Backspace | Back to the start |
| ← / → | Seek 5 s (with Shift, 30 s) |
| ↑ / ↓ | Replay speed. While following the path, the whole path's speed |
| , / . | Step one frame back or forward, at the export frame rate |
| [ / ] | Jump to the previous or next camera key, wrapping around at the ends |
| K | Add a camera key with the current pose at the current replay time |
| Del | Delete the camera key nearest the playhead |
| Ctrl+Z / Ctrl+Y | Undo / redo any change to keys, bookmarks or hidden objects (Ctrl+Shift+Z also redoes) |
| Ctrl+C / Ctrl+V | Copy the key nearest the playhead / paste its pose at the playhead |
| P | Follow the camera path on/off |
| V | Show the path line on/off (never shown while following) |
| N | Record a take at the current speed: fly by hand while the replay plays, and N again turns it into camera keys |
| J | Follow the character or vehicle at the screen center, keeping your offset, and look around freely. J again detaches |
| Shift+J | Mount on it rigidly, so the offset and angle turn with it (for example under a ship's hull) |
| B | Orbit the object at the screen center. B again stops |
| L | Keep the view on the object at the screen center, on a path too. L again stops |
| U | Hide the object at the screen center from the shot. Ctrl+Z or the menu brings it back |
| 1-9 / Ctrl+1-9 | Jump to / save a camera bookmark (saved with the replay) |
| O | Original camera (what the recorder saw) |
| H | High-rate motion of characters, vehicles and other moving objects on/off, to compare smoothness |
| G | Composition guides (thirds, golden, center, safe areas, 2.39:1, 9:16, horizon). Hold to hide or show the guide; it stays that way |
| T | Hide or show the timeline and info |
| F9 | Screenshot, without any Replay Mod overlay |
| F10 | Export video, or cancel a running export |

Hold ←, →, [, ], comma or period to keep going. Sound pitch follows the replay speed.

### Camera paths

To make a path, fly to a pose, move the timeline and press K. Repeat for each pose. Press P and play, and the camera glides through every key on a smooth curve. The key timing sets its speed, and FOV and roll animate too.

- **On the timeline:** keys show as squares above it. Click one to jump to it, double-click to put the camera exactly on it, drag it to retime it, and right-click to delete it. Shift+click selects several, which then drag and delete together. Dragging snaps to markers unless you hold Alt.
- **Exact values:** the Tab menu has fields for the key's time, FOV and roll. Enter applies.
- **Re-posing:** pressing K at a key's time replaces that key's pose.
- **Easing:** each move from one key to the next can be Smooth, Ease in/out (stops at both keys), Ease in, Ease out, Linear or Hold. Set it in the Tab menu.
- **Speed track:** each camera key has a replay speed (Tab menu, under the key's ease). While following the path, the replay speeds up and slows down between keys and the camera keeps its pace, so a slow-motion stretch doesn't slow the camera move.
- **Time of day and weather:** a key can set either. Time blends to the next key that sets it, the short way around the clock. Weather changes at the key and stays. Both apply whether or not you follow the path, and they override the Time and weather section.
- **In the world:** each key shows as a small numbered wireframe camera pointing where it looks. Its notch marks the top, so you can see roll. Keys and the path are drawn after depth of field and other effects, so they stay sharp, and they show through terrain.
- **Path colors:** the path line runs from green (slowest) to red (fastest camera). The Tab menu shows the top speed in m/s.
- **Takes in slow motion:** slow the replay down, press N and fly. You get more time to steer and just as much detail. The take keeps the speed you flew it at, and the whole path speed (↑/↓ while following, or the Tab menu) plays it at any other speed.
- **Moving with an object:** *Path moves with target* in the Tab menu ties the path to the object the camera is attached to, or to the one at the screen center. The keys then ride along with it. *Moves and turns with it* turns them with it too, for a camera path around a moving ship.
- **Saving:** Replay Mod saves keys and bookmarks automatically next to the replay as `<name>.camera.txt`.

### Tab menu

Type in the search box at the top to show only the sections that have what you're after, with the words you typed highlighted. Enter, Tab or a click leaves the box. Right-click a slider to put it back to its default. Drag the menu's bottom-right corner to resize it.

- **Framing and focus:** has depth of field, which in a replay is on only when this switch is on, whatever the game's graphics setting says.
- **Picture:** changes brightness, exposure, temperature, saturation, contrast, bloom, lens dirt and sun shafts.
- **Camera presets:** keep all of the settings above.
- **Time and weather:** wind follows the recording unless *Set the wind* is on, which sets its direction and strength.
- **Hide from the shot:** hides the aimed object (U), all players, what players hold, and projectiles. Projectiles that a mod draws its own way may not count.
- **Players:** lists the recorded players. *Go to* puts the camera behind one, *Follow* follows them, and *Eyes* shows their view.
- **HUD:** shows health bars and damage numbers without the rest of the HUD, or turns on the normal game HUD (same as Ctrl+F3).

### Timeline

A click seeks. Dragging from the playhead scrubs. Dragging anywhere else selects a section for Trim, and a right-click clears it.

Recordings mark deaths, kills, bosses (seen and defeated), teleports and your own marks as colored ticks on the timeline. Hover one, or a camera key, for details.

### Targets

You can follow, mount, orbit or look at arrows, bolts, spears, bombs, spells, meteors and catapult shots by aiming near them and pressing the associated key.

### Trim

In the Tab menu, select a section (or mark a start and an end) and save that part as a new, shorter replay. Camera keys in it are included.

### Screenshots

F9 takes a screenshot. The Export section has brightness, exposure and a camera flash, a light on the camera for the moment of the shot. *Keep it lit* helps line it up.

### Export

F10 renders the camera path, playhead to end, the section selected on the timeline, or the whole replay at a fixed 24, 30, 60, 120 or 144 fps, optionally at 2x or 3x the window size, so the video is smooth however slow your PC is. Sound is recorded in a second, real-time pass. Files go to `...\ReplayMod\exports`.

Export needs FFmpeg and writes an MP4. Replay Mod looks for FFmpeg in the usual places. If it isn't found, the Tab menu has an **Install FFmpeg** button (WinGet), or you can set `FfmpegPath` in the config.

## Configuration

Settings are in `BepInEx/config/com.gargantua.replaymod.cfg`, or in the F1 menu if you have a configuration manager mod installed. The sections are:

- **Recording:** the record, mark and Replay Buffer keys, what gets recorded and at what rate.
- **Playback:** replaying other mods' RPCs, LOD bias, lowest sound pitch and terrain load radius.
- **Export:** frame rate, resolution, audio and `FfmpegPath`.
- **UI:** the Tab menu's size, closed sections and camera presets.

## Compatibility

- Only the player recording or watching needs Replay Mod. Other players on a server don't.
- Replay Mod replays other mods' RPCs aimed at recorded objects, such as weapon fire and effects. If a mod misbehaves in replays, turn off `ReplayModRpcs`.
- `RecordPose` records your character's final bone pose, so animation mods replay as you saw them.
- Mods that make teleporting faster, and the game's debug mode, can make replays unstable while you watch them. Normal play isn't affected.

## Known limits

- Ragdolls and falling trees are simulated again during playback, so they may not match the original.
- After a far teleport, the replay pauses while the new area loads. Press Space to go on.
- A trimmed clip or a saved Replay Buffer shows the map as it was when recording started, plus what was explored during the clip. What was explored in between is missing.
- Beyond the recorded zones, you see untouched nature from the seed.
- The replay world shows up as `ReplayMod` in your world list.
