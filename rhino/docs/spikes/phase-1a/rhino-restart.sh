#!/bin/bash
# Quit Rhino 8 discarding any unsaved document (clicks Delete on the keep-document sheet), then relaunch and open a new model.
RC="/Applications/Rhino 8.app/Contents/Resources/bin/rhinocode"
if pgrep -x Rhinoceros >/dev/null; then
  osascript -e 'tell application "Rhino 8" to quit' >/dev/null 2>&1 &
  for i in $(seq 1 30); do
    pgrep -x Rhinoceros >/dev/null || break
    osascript <<'AS' >/dev/null 2>&1
tell application "System Events"
  tell process "Rhinoceros"
    repeat with w in windows
      try
        set ec to entire contents of sheet 1 of w
        repeat with i from 1 to (count of ec)
          set e to item i of ec
          try
            if (name of e as text) is "Delete" then
              click e
              exit repeat
            end if
          end try
        end repeat
      end try
    end repeat
  end tell
end tell
AS
    sleep 1
  done
fi
pgrep -x Rhinoceros >/dev/null && { echo "rhino did not quit"; exit 1; }
open -a "Rhino 8"
until "$RC" list 2>/dev/null | grep -q remotepipe; do sleep 2; done
sleep 4
osascript -e 'tell application "Rhino 8" to activate' -e 'tell application "System Events" to tell process "Rhinoceros" to click button "New Model" of window 1' >/dev/null 2>&1
sleep 4
"$RC" list
