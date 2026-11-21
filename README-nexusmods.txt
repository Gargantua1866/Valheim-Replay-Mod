[center][size=6][b]Replay Mod[/b][/size]
[size=3]Record Valheim, then watch it again with a free camera and export it as video[/size]

[youtube]tQ8CQNI6xOA[/youtube][/center]

Replay Mod records everything that happens around you in Valheim. Open the recording afterward in a separate replay world, fly a free camera through it, build camera paths and export the result as an MP4.

[size=5][b]Features[/b][/size]
[list]
[*]Record in single player or multiplayer, as host or as client, by hand or with a Replay Buffer that saves the last few minutes on a key press.
[*]Fly a free camera with FOV and roll, or follow, mount, orbit or look at any character, vehicle or projectile.
[*]Build camera paths from keys, with easing, per-key FOV, roll, replay speed, time of day and weather.
[*]Change the replay's speed, time of day, weather, wind, depth of field and picture settings.
[*]Trim a replay into a shorter one.
[*]Export video at a fixed frame rate, with sound, and take screenshots without any overlay.
[/list]

[size=5][b]Requirements[/b][/size]
[list]
[*][b]BepInEx[/b], to load the plugin
[*][b]FFmpeg[/b], only for video export. Replay Mod doesn't include it; see Export below.
[*]The [b]terrain generation mod[/b], such as Riverheim, that a replay was recorded with, to watch that replay. Without it the replay shows vanilla terrain.
[/list]

[size=5][b]Installation[/b][/size]
[list=1]
[*]Install BepInEx.
[*]Extract the zip into your Valheim folder, so the DLL ends up at [b]BepInEx\plugins\ReplayMod\ReplayMod.dll[/b]. Vortex can install it too.
[/list]
With a mod manager such as Gale or r2modman, install Replay Mod from Hexium instead, or import the mod-manager zip from the [url=https://github.com/Gargantua1866/Valheim-Replay-Mod/releases]GitHub releases[/url].

[size=5][b]Recording[/b][/size]
In game, press [b]Ctrl+F10[/b] to start/stop recording. Press [b]Ctrl+F8[/b] while recording or buffering to put a mark on the timeline.

[b]Replay Buffer:[/b] enable it in the Replays tab or with [b]Ctrl+Alt+F10[/b] in game. [b]Ctrl+Shift+F10[/b] then saves the last few minutes while you play. Change the length in the Replays tab or the config. The buffer records all the time, so it uses as many resources as a normal recording does.

A recording holds:
[list]
[*]players, creatures, ships and projectiles the game has loaded around you
[*]scenery (trees, rocks, buildings, terrain edits) in the 3x3 zones around you
[*]attacks and effects
[*]your hotbar, food, status effects and explored map, shown in the replay when the game HUD is on
[*]your camera, the time of day, the weather, the wind and the waves
[/list]
Recordings go to [b]%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\ReplayMod\replays[/b]. Each .replay file has a .json next to it with size and timing statistics.

[size=5][b]Watching a replay[/b][/size]
[list=1]
[*]On the main menu, pick a character and press Start.
[*]Open the [b]Replays[/b] tab, pick a replay and press [b]Play[/b]. The panel beside the list shows the replay's thumbnail and lets you rename or copy it.
[*]The replay opens paused, in a temporary world built from the recorded world's seed. Everything still loads while paused. Nothing is saved to that world or to your character.
[*]To leave, use [b]Escape, then Logout[/b], or [b]Exit replay[/b] in the Tab menu. The Tab menu can also leave without saving, which puts camera keys and bookmarks back to how they were when the replay opened. Leaving with saving keeps the playhead, camera position and camera settings, and the replay opens there next time.
[/list]

[size=5][b]Keys[/b][/size]
These keys work inside a replay and can't be changed.
[list]
[*][b]Mouse:[/b] look
[*][b]W A S D:[/b] move
[*][b]E / Q:[/b] up / down
[*][b]Shift / Ctrl:[/b] fast / slow
[*][b]Wheel:[/b] move speed
[*][b]Alt + wheel, R / F:[/b] FOV (R zooms in, F zooms out)
[*][b]Z / C:[/b] roll
[*][b]X:[/b] reset roll and turn an inverted view upright
[*][b]Tab:[/b] free the cursor and open the menu. Click or drag the timeline at the bottom to seek
[*][b]Space:[/b] pause / play
[*][b]Backspace:[/b] back to the start
[*][b]Left / Right arrows:[/b] seek 5 s (with Shift, 30 s)
[*][b]Up / Down arrows:[/b] replay speed. While following the path, the whole path's speed
[*][b]Comma / period:[/b] step one frame back or forward, at the export frame rate
[*][b]Square brackets:[/b] jump to the previous or next camera key, wrapping around at the ends
[*][b]K:[/b] add a camera key with the current pose at the current replay time
[*][b]Del:[/b] delete the camera key nearest the playhead
[*][b]Ctrl+Z / Ctrl+Y:[/b] undo / redo any change to keys, bookmarks or hidden objects (Ctrl+Shift+Z also redoes)
[*][b]Ctrl+C / Ctrl+V:[/b] copy the key nearest the playhead / paste its pose at the playhead
[*][b]P:[/b] follow the camera path on/off
[*][b]V:[/b] show the path line on/off (never shown while following)
[*][b]N:[/b] record a take at the current speed: fly by hand while the replay plays, and N again turns it into camera keys
[*][b]J:[/b] follow the character or vehicle at the screen center, keeping your offset, and look around freely. J again detaches
[*][b]Shift+J:[/b] mount on it rigidly, so the offset and angle turn with it (for example under a ship's hull)
[*][b]B:[/b] orbit the object at the screen center. B again stops
[*][b]L:[/b] keep the view on the object at the screen center, on a path too. L again stops
[*][b]U:[/b] hide the object at the screen center from the shot. Ctrl+Z or the menu brings it back
[*][b]1-9 / Ctrl+1-9:[/b] jump to / save a camera bookmark (saved with the replay)
[*][b]O:[/b] original camera (what the recorder saw)
[*][b]H:[/b] high-rate motion of characters, vehicles and other moving objects on/off, to compare smoothness
[*][b]G:[/b] composition guides (thirds, golden, center, safe areas, 2.39:1, 9:16, horizon). Hold to hide or show the guide; it stays that way
[*][b]T:[/b] hide or show the timeline and info
[*][b]F9:[/b] screenshot, without any Replay Mod overlay
[*][b]F10:[/b] export video, or cancel a running export
[/list]
Hold the arrows, square brackets, comma or period to keep going. Sound pitch follows the replay speed.

[size=5][b]Camera paths[/b][/size]
To make a path, fly to a pose, move the timeline and press K. Repeat for each pose. Press P and play, and the camera glides through every key on a smooth curve. The key timing sets its speed, and FOV and roll animate too.
[list]
[*][b]On the timeline:[/b] keys show as squares above it. Click one to jump to it, double-click to put the camera exactly on it, drag it to retime it, and right-click to delete it. Shift+click selects several, which then drag and delete together. Dragging snaps to markers unless you hold Alt.
[*][b]Exact values:[/b] the Tab menu has fields for the key's time, FOV and roll. Enter applies.
[*][b]Re-posing:[/b] pressing K at a key's time replaces that key's pose.
[*][b]Easing:[/b] each move from one key to the next can be Smooth, Ease in/out (stops at both keys), Ease in, Ease out, Linear or Hold. Set it in the Tab menu.
[*][b]Speed track:[/b] each camera key has a replay speed (Tab menu, under the key's ease). While following the path, the replay speeds up and slows down between keys and the camera keeps its pace, so a slow-motion stretch doesn't slow the camera move.
[*][b]Time of day and weather:[/b] a key can set either. Time blends to the next key that sets it, the short way around the clock. Weather changes at the key and stays. Both apply whether or not you follow the path, and they override the Time and weather section.
[*][b]In the world:[/b] each key shows as a small numbered wireframe camera pointing where it looks. Its notch marks the top, so you can see roll. Keys and the path are drawn after depth of field and other effects, so they stay sharp, and they show through terrain.
[*][b]Path colors:[/b] the path line runs from green (slowest) to red (fastest camera). The Tab menu shows the top speed in m/s.
[*][b]Takes in slow motion:[/b] slow the replay down, press N and fly. You get more time to steer and just as much detail. The take keeps the speed you flew it at, and the whole path speed (Up/Down arrows while following, or the Tab menu) plays it at any other speed.
[*][b]Moving with an object:[/b] "Path moves with target" in the Tab menu ties the path to the object the camera is attached to, or to the one at the screen center. The keys then ride along with it. "Moves and turns with it" turns them with it too, for a camera path around a moving ship.
[*][b]Saving:[/b] Replay Mod saves keys and bookmarks automatically next to the replay as <name>.camera.txt.
[/list]

[size=5][b]Tab menu[/b][/size]
Type in the search box at the top to show only the sections that have what you're after, with the words you typed highlighted. Enter, Tab or a click leaves the box. Right-click a slider to put it back to its default. Drag the menu's bottom-right corner to resize it.
[list]
[*][b]Framing and focus:[/b] has depth of field, which in a replay is on only when this switch is on, whatever the game's graphics setting says.
[*][b]Picture:[/b] changes brightness, exposure, temperature, saturation, contrast, bloom, lens dirt and sun shafts.
[*][b]Camera presets:[/b] eight slots that keep all of the settings above. Click a preset's name to rename it.
[*][b]Time and weather:[/b] wind follows the recording unless "Set the wind" is on, which sets its direction and strength.
[*][b]Hide from the shot:[/b] hides the aimed object (U), all players, what players hold, and projectiles. Projectiles that a mod draws its own way may not count.
[*][b]Players:[/b] lists the recorded players. "Go to" puts the camera behind one, "Follow" follows them, and "Eyes" shows their view.
[*][b]HUD:[/b] shows health bars and damage numbers without the rest of the HUD, or turns on the normal game HUD (same as Ctrl+F3).
[/list]

[size=5][b]Timeline[/b][/size]
A click seeks. Dragging from the playhead scrubs. Dragging anywhere else selects a section for Trim, and a right-click clears it.

Recordings mark deaths, kills, bosses (seen and defeated), teleports and your own marks as colored ticks on the timeline. Hover one, or a camera key, for details.

[b]Targets:[/b] you can follow, mount, orbit or look at arrows, bolts, spears, bombs, spells, meteors and catapult shots by aiming near them and pressing the associated key. Missiles that mods fly on each machine without a network identity work too: follow, mount on or orbit one, and the camera lets go where it explodes. [b]LocalTargets[/b] in the config lists which kinds count (by component name; Missile by default).
[b]Trim:[/b] in the Tab menu, select a section (or mark a start and an end) and save that part as a new, shorter replay. Camera keys in it are included.
[b]Screenshots:[/b] F9 takes a screenshot. The Export section has brightness, exposure and a camera flash, a light on the camera for the moment of the shot. "Keep it lit" helps line it up.

[size=5][b]Export[/b][/size]
F10 renders the camera path, playhead to end, the section selected on the timeline, or the whole replay at a fixed 24, 30, 60, 120 or 144 fps, optionally at 2x or 3x the window size, so the video is smooth however slow your PC is. Sound is recorded in a second, real-time pass. Files go to [b]...\ReplayMod\exports[/b].

Export needs FFmpeg and writes an MP4. Replay Mod looks for FFmpeg in the usual places. If it isn't found, the Tab menu has an [b]Install FFmpeg[/b] button (WinGet), or you can set [b]FfmpegPath[/b] in the config.

[size=5][b]Configuration[/b][/size]
Settings are in [b]BepInEx\config\com.gargantua.replaymod.cfg[/b], or in the F1 menu if you have a configuration manager mod installed. The sections are:
[list]
[*][b]Recording:[/b] the record, mark and Replay Buffer keys, what gets recorded and at what rate.
[*][b]Playback:[/b] replaying other mods' RPCs, local camera targets, LOD bias, lowest sound pitch and terrain load radius.
[*][b]Export:[/b] frame rate, resolution, audio and FfmpegPath.
[*][b]UI:[/b] the Tab menu's size, closed sections and camera presets.
[/list]

[size=5][b]Compatibility[/b][/size]
[list]
[*]Only the player recording or watching needs Replay Mod. Other players on a server don't.
[*]Replay Mod replays other mods' RPCs aimed at recorded objects, such as weapon fire and effects. If a mod misbehaves in replays, turn off [b]ReplayModRpcs[/b].
[*][b]RecordPose[/b] records your character's final bone pose, so animation mods replay as you saw them.
[*][b]Terrain generation mods such as Riverheim must be installed to watch a replay recorded with them.[/b] Replay Mod rebuilds the world from its seed, so without the mod the replay shows vanilla terrain. Mods built on Worldhalla (Riverheim is one) are supported. Recordings made before 0.1.3 take the generator from your saved world of the same name and seed, if you have it.
[*]Mods that make teleporting faster, and the game's debug mode, can make replays unstable while you watch them. Normal play isn't affected.
[/list]

[size=5][b]Known limits[/b][/size]
[list]
[*]Ragdolls and falling trees are simulated again during playback, so they may not match the original.
[*]After a far teleport, the replay pauses while the new area loads. Press Space to go on.
[*]A trimmed clip or a saved Replay Buffer shows the map as it was when recording started, plus what was explored during the clip. What was explored in between is missing.
[*]Beyond the recorded zones, you see untouched nature from the seed.
[*]The replay world shows up as ReplayMod in your world list.
[/list]

[size=5][b]Source and license[/b][/size]
Replay Mod's source: [url=https://github.com/Gargantua1866/Valheim-Replay-Mod]github.com/Gargantua1866/Valheim-Replay-Mod[/url] (MIT)
