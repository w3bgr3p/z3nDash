# zpXml

The `/zpxml.html` page renders a ZennoPoster XML template as an executable graph,
supports structural editing, and runs an embedded debugging session.

## Open and inspect

The **open…** button uses the system dialog and keeps the full template path.
The runtime needs it to resolve `.env`, shared code, and other project files.
The graph supports zooming, panning, label/ID search, and switching between the
template canvas coordinates and a vertical layout.

The step panel shows the action type, parameters, code, caption, flags and
transitions. Template settings expose project `InputSettings` and profile data.

## Edit

In **edit** mode you can:

- change branch parameters, code, captions, flags and case keys;
- reorder branches or move them between blocks;
- extract a branch into a new block;
- delete a branch while redirecting incoming transitions;
- set transitions by dragging their ports;
- move blocks on the canvas;
- undo and redo with `Ctrl+Z` and `Ctrl+Y`.

The page warns before leaving with unsaved changes. **save .xml** downloads the
edited document with its original XML declaration and encoding.

## Debug

**start** creates one server-side session with a browser. **step**, **run**,
**pause**, run-to-branch, and **stop** then control execution. The current branch,
variables, profile and log update over SSE. The graph follows execution only
when the current branch has moved outside the visible area.

Debugging uses the `/dbg/*` routes. One session can run in the application at a
time, and it is released automatically after 10 minutes of inactivity.
