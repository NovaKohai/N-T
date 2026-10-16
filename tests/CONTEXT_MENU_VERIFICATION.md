# Context Menu Verification

## Setup
1. Build and run NeoDM App: `dotnet run --project src/NDM.App`
2. Add at least one test download (any URL)
3. Wait for download to complete OR pause it

## Test Cases

### 1. Context Menu Appears
- Action: Right-click on any download in the list
- Expected: Context menu appears with 6 items:
  - Open file
  - Open folder
  - Copy path
  - -------- (separator)
  - Delete file
  - Remove from list

### 2. Menu Item States - Active Download
- Setup: Right-click on a downloading/paused job
- Expected States:
  - Open file: Disabled (not completed)
  - Open folder: Enabled (directory exists)
  - Copy path: Enabled
  - Delete file: Disabled (file not yet complete)
  - Remove from list: Enabled

### 3. Menu Item States - Completed Download
- Setup: Right-click on a completed download
- Expected States:
  - Open file: Enabled (completed + file exists)
  - Open folder: Enabled
  - Copy path: Enabled
  - Delete file: Enabled (file exists)
  - Remove from list: Enabled

### 4. Open File
- Setup: Completed download
- Action: Context menu > Open file
- Expected: 
  - Security confirmation dialog appears (same as existing Open button)
  - On "Yes", file opens in default application
  - Status bar shows "File opened."

### 5. Open Folder
- Setup: Any download
- Action: Context menu > Open folder
- Expected:
  - Explorer opens to download directory
  - Status bar shows "Download folder opened."

### 6. Copy Path
- Setup: Any download
- Action: Context menu > Copy path
- Expected:
  - No visible dialog
  - Status bar shows "Path copied to clipboard."
  - Verify: Paste into Notepad shows full file path

### 7. Delete File - Confirmation
- Setup: Completed download with existing file
- Action: Context menu > Delete file → Click "No"
- Expected:
  - Confirmation dialog: "Delete '[filename]' from disk AND remove from NeoDM?\n\nThis cannot be undone. The file will be permanently deleted."
  - Title: "Delete file"
  - On "No", dialog closes, no changes

### 8. Delete File - Success
- Setup: Completed download with existing file
- Action: Context menu > Delete file → Click "Yes"
- Expected:
  - File deleted from disk (verify in Explorer)
  - Download removed from NeoDM queue
  - Status bar shows "File deleted and download removed."

### 9. Delete File - File Not Found
- Setup: Delete the completed file manually from disk first
- Action: Context menu > Delete file
- Expected:
  - Menu item is disabled (grayed out)
  - OR if file disappeared after menu opened: Status shows "The file is no longer in its saved location."

### 10. Delete File - File In Use
- Setup: Open completed file in another application (lock it)
- Action: Context menu > Delete file → "Yes"
- Expected:
  - Error dialog: "The file could not be deleted. It may be in use or protected.\n\nThe download was not removed from the queue."
  - File still exists
  - Download still in queue

### 11. Remove from List
- Setup: Any download (active or completed)
- Action: Context menu > Remove from list
- Expected:
  - Same behavior as existing Remove button
  - Confirmation dialog appears
  - On "Yes": Download removed from queue, file kept on disk

### 12. Keyboard Navigation
- Action: Select download, press keyboard context menu key (usually right of spacebar)
- Expected: Context menu appears at selected row

## Pass Criteria
- All 12 test cases pass
- No crashes or exceptions
- Menu states correctly reflect download status and file existence
- Destructive operations require confirmation
- Non-destructive operations work silently
- Status bar provides feedback for all actions
