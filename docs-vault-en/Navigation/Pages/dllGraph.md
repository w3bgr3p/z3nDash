# dllGraph

dllGraph builds a graph of C# code from sources or from a DLL.

On the page, **Choose DLL** opens a system dialog for picking a `.dll`.
Once a file is chosen, press **Generate**.

## Input

`POST /dllGraph/generate?repoPath=...` accepts:

- a path to a single DLL;
- a folder of DLLs;
- a folder of C# sources.

When reading sources, the `bin` and `obj` folders are skipped.

## Result

The generated HTML is held in process memory. `GET /dllGraph` returns the last graph built.

After a z3nDash restart the graph has to be built again.

## API

- `GET /dllGraph/pick`
- `POST /dllGraph/generate`
- `GET /dllGraph`
