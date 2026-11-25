# Nexus Manager for PC

Manage a Nexus USB game drive on Windows x64. Browse installed games, preview compression savings, prepare your own game packages, install HATS packs and format USB drives.

[Download the Windows installer](https://github.com/TR4UNEXUS/Nexus-Manager-PC/releases/latest/download/Nexus-Manager-PC-win-x64-Setup.exe)

Open the installer, then start **Nexus Manager for PC** from the Windows Start menu. From version 0.1.9, Manager checks GitHub when it opens. When a newer release is available, choose **Yes** to download, install and reopen Manager. For a manual check, choose **File → Check for updates**. If you use an older version, run the Windows installer once to get the updater.

For Switch sync, use **Nexus HATS NX1** (any edition) and **Switch Nexus Manager 0.2.33 or later**. Use Switch Manager **0.2.39 or later** for compressed game replacements. The Switch checks the HATS pack and USB service before it offers to sync.

## Install from PC

1. Connect the exFAT or FAT32 USB game drive to the PC. Open NexusManagerPC.exe and choose your own local prod.keys once. The app saves an encrypted copy of the selected prod.keys for the current Windows account. You may remove the original drive afterward. Choose prod.keys again to replace it. The USB library opens automatically when the drive is detected. If several game drives are connected, select the one you want.
2. Open **Install games**, drop files or folders onto the window, or choose **Add folder**. Manager scans subfolders for NSP, NSX, NSZ, XCI and XCZ packages and skips duplicates. Review the queue and choose **Review and install**. Each new content file is checked against the package and read back before publication. Existing matching files are reused. FAT32 requires every individual content file to fit its 4 GiB limit.
3. Safely eject the drive in Windows. Connect it to your Switch and open Switch Manager from HOME, or start its NRO in full-memory mode.
4. Switch Manager 0.2.39 detects prepared games on startup. For manual import, choose **Installed games → Import from PC → Add games to HOME**. After the import finishes, fully restart the Switch before launching a newly added game. The Switch registers the existing USB files and imports eligible original tickets through its ticket service. Games requiring permissions need either a ticket already accepted on that system or a valid original ticket/certificate pair for it. Personalised permissions remain subject to the console's own checks.

In **HATS Installer**, select the HATS pack and Switch SD card, choose a PC backup folder, then select **Install selected pack**. The tab downloads the chosen release from TR4UNEXUS/Nexus-USB-HATS, backs up the files it replaces and protects your USB game mappings, games and mods. The separate FAT32 action erases a card after confirmation.

In **USB Formatter**, choose a USB drive. **Keep files** lets you inspect its partitions, convert a supported exFAT layout or recover an interrupted conversion. **Erase & format** creates a fresh exFAT drive and asks for confirmation. Windows may ask for administrator permission for the disk operation. Progress and results appear in the tab.

New files are staged per package. If a package fails or you cancel, Manager removes that package's unfinished files. Earlier completed packages and files already on the drive are kept. Reconnecting a drive after an interruption lets Manager clean up staging or finish an already verified commit.

An XCI may have its private cartridge certificate removed. Only game files in its secure partition are imported. An NSP or NSZ can also omit the ticket certificate. Files are still prepared. If those game files require a ticket, the Switch needs its existing permission or an original ticket and certificate from your backup. Content whose bytes disagree with its manifest is rejected.

## Manage USB games

The **USB library** opens in a resizable Windows window. Each row shows a small game icon, installed version, update count, DLC count, size and file status. Select a game to see its individual base game, updates and DLC, with the title ID, version and size of each. Drag the divider to adjust the details area. The library checks required file presence and sizes. It excludes optional update-delivery fragments, as Switch Manager does. Open a component reporting a file issue to see the affected filenames.

Select one or more games and choose **Delete selected**. After confirming, reconnect the drive to the Switch and open **Nexus Manager 0.2.38 or later** from HOME. Manager picks up the deletion, updates the HOME icon and removes tickets for deleted content once it verifies that no installed copy needs them. Other installed copies and saves are kept. To retry, open **Installed games → Import from PC**. An older PC deletion record can update HOME but carries no ticket list. Reopen the PC's USB library after an interrupted deletion to finish recovery.

## Compress games

Manager automatically scans savings when it opens a connected USB library. You can browse the library or switch tabs while it runs. If an update or DLC lacks a usable permission backup, Manager tries the base game alone and labels the result **Base only**. If the base also cannot be read, its estimate is unavailable. Scanning never asks you to select an NSP.

Select games in **USB library → Scan savings** to see their current size, estimated compressed size and the percentage and bytes you could save. Clear the selection to scan the whole list. Manager samples the installed files on USB. **Install games → Scan savings** previews the packages in your queue, even before connecting a drive. Package estimates include the selected update and DLC. The actual free space gained can differ when games share files. One progress bar follows the scan, preparation, compression, verification and USB installation.

The **Compression** column updates when the library opens. **Nexus compressed** identifies games compressed by PC Manager 0.1.7 or later, and earlier replacements whose PC receipt still matches the installed files. **Nexus base** means a separate update was installed alongside that base. **Compressed** identifies a base game with the bundled compressor’s mark and compressed data. **Compressed base** means a separate update is also installed. **Compressed data** means the format is compressed but the source is unconfirmed. **Compressor mark** identifies a rebuilt base with no detected compressed base data. **No marker** and **Unknown** make no claim about which app produced the game.

For a new USB install, select **Install games → Compress before installing**. Add the base game, its matching update and any DLC together. NSP, NSX, NSZ, XCI and XCZ are accepted. Protected files need the original ticket and certificate. Manager builds and verifies a smaller package on the PC before writing to USB. Allow free space on the PC for the working files.

For an installed USB game, select **USB library → Compress game** and confirm the drive. Manager uses temporary space on the PC for the original and compressed packages. It stages and checks the new files on USB before replacing the installed copy, then clears its working files. Allow free space on USB for the compressed copy as well. If the drive disconnects, reopen the USB library on the same PC and drive to finish recovery. Open **Switch Manager 0.2.39 or later** to sync the changed game with HOME. If the drive lacks a usable permission backup, Manager asks you to select your original package.

Compression merges the selected update into the rebuilt base game. Keep your original packages so you can rebuild with future updates. A separate update for the original base can prevent the compressed game from starting. The upstream compressor requires compatible signature patches and Switch system software 12.0.0 or newer. [hacBrewPackLZ documentation](https://gitlab.com/ismaellozanoperezserrano/hacbrewpacklz/-/blob/c7ce8f833d9901c30645b94afaca4dd8c08be845/README.md)

## Build and test

The desktop app targets .NET 8. The separate compression engine needs the .NET 10 SDK at build time. Run `.\Windows\Publish.ps1 -OutputDirectory .\publish` to build both executables. If the .NET 10 SDK is installed separately, pass its executable with `-CompressionDotnet`. To build the installer, also pass `-InstallerCompiler` with the path to Inno Setup 6's ISCC.exe.

For an app update, increase the version in Windows/NexusManagerPC.csproj, build the installer and attach **Nexus-Manager-PC-win-x64-Setup.exe** to the matching stable GitHub release (for example, `v0.1.9`). Set that release as latest. Manager checks the release version, download size and GitHub SHA-256 digest before starting setup.

Run `dotnet run --project Tests -c Release` for transfer and ticket-framing tests. For the full synthetic library test, generate input with `python Tests/make_fixture.py <empty-directory>` (requires Python cryptography). Pass the generated fixture.keys, fixture.nsp and shared.nsp to the test runner in that order. Test files hold random test keys and synthetic data. An actual console is needed to validate ticket acceptance and HOME behaviour.

LibHac 0.19.0 reads package metadata. Windows USB device identification is shared with Nexus HATS Desktop.

