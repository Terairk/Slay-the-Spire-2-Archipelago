> [!WARNING]
> Both Slay the Spire II and this Archipelago Mod are in active development. As such, while this _is_ playable, expect there to be bugs and limited features. We appreciate your playtesting!

# Changelist

<This will be manually updated by me when the script is done running>

# Mod Information

## Pre-Requisites

- **Your host MUST use Archipelago Client v0.6.7+**.
- This version of the mod is intended to be used for **v0.107.1**  of Slay the Spire II
  - While we have experimental support for the public beta branch **v0.111.0**, we recommend **the "Default Public/Main Version"** of the game to most players. Since the beta branch is updated more frequently and is more volatile, it's less likely to be bug-free. Please use it at your own risk. However numerous players have used it just fine, @Terairk is the maintainer for this and he mostly develops here.
  - We will do our best to keep up with game updates as they release, so please be patient when encountering issues.

## Installing the Mod from Steam Workshop (Recommended)

1. Ensure that you have [RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) installed. The easiest way to obtain it is from Steam Workshop.
2. Subscribe to the [Slay the Spire II Archipelago Client](https://steamcommunity.com/sharedfiles/filedetails/?id=3748826296) on Steam Workshop.
3. Make sure that any other unnecessary mods are turned off, unless you're trying to use a specific modded character for your Archipelago session.
4. Start the game

## Manually Installing the Mod from GitHub

1. Ensure that you have [RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) installed. The easiest way to obtain it is from Steam Workshop.
2. Download the "sts2-client.zip" from the [Releases](https://github.com/dlueben1/Slay-the-Spire-2-Archipelago/releases/latest) section of the Repo
3. Go to your Slay the Spire II directory (In Steam, click "Browse Local Files")
4. If a folder called `mods` does not exist, create it
5. Unzip the **contents** of `sts2-client.zip` into `mods`

- If you've done this step correctly, your directory structure should look like this: `/<slay-the-spire-2-local-files>/mods/Archipelago/` and the contents of that folder should be a bunch of `.dll` files and a `.pck` file (there may be more files too, please don't touch anything in this folder)

6. Start the game

### Additional Steps for **Hosts**

7. Download `spire2.apworld`
8. Open your Archipelago Launcher
9. Click "Install APWorld"
10. Select `spire2.apworld` in the file dialog that pops up
11. **Restart the Archipelago Launcher**
12. Now you should be able to properly host/generate an Archipelago Session with StS 2

- If you want to use `archipelago.gg` to host the game, generate it locally first following the steps above, then upload the `.zip` file from the `output` folder in your Archipelago installation

> [!IMPORTANT]
> You need to use Archipelago Version 0.6.7+ and CANNOT use earlier versions of Archipelago with this mod!

## Known Issues/Limitations

- Switching Slay the Spire II branches during an active run may make that run's checkpoint incompatible with the other branch.

## Common Q&A

### Will this mess with my unmodded Save File?

No.

### I installed the AP World but it's not working

Is your Archipelago Launcher v0.6.7 or later? If not it **won't work**.
