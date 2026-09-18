# History regression checks

Run from the repository root:

```sh
dotnet run --project tests/History/History.csproj -c Release
dotnet run --project tests/HistoryThumbnails/HistoryThumbnails.csproj -c Release
dotnet build NAITool.sln -c Debug -p:Platform=x64 --no-restore -v:minimal
```

The first suite uses 200,000 synthetic paths and 10,000 random row accesses. It checks the full
scroll range, bounded row cache, gallery packing, date boundaries, immutable snapshots, pending
completion, insertion/deletion anchor lookup, and cancellable disk scanning. It does not create
200,000 PNGs or instantiate WinUI controls. Scanner fixtures use an isolated temporary folder.

The Windows-only thumbnail suite creates temporary 4096-pixel PNGs and exercises the production
decoder, including high-DPI sizing, corrupt input, cancellation, and stream disposal.

## Manual WinUI acceptance

These checks still require the real application; the model tests do not validate native
ScrollViewer layout, pointer input, frame rate, or pixel-offset restoration.

- With a large history, drag the scrollbar to the middle and end, then rapidly reverse direction.
  Verify the final image remains reachable and loading settles on the current viewport.
- Switch between the generation sidebar and gallery, then resize across gallery column boundaries.
  Verify the visible image stays near the same vertical position and horizontal scrolling stays off.
- With automatic return-to-top disabled, generate or delete an image while viewing older history.
  Verify the viewport does not jump to the beginning. With it enabled, verify generation returns to top.
- Change dates and switch workspaces while thumbnails are loading; verify no recycled cell shows
  another image and corrupt files stop displaying a loading spinner.
- Repeat in light/dark themes and at 100%, 150%, and 200% display scaling, including moving between
  monitors with different DPI. Verify uncropped thumbnails and an unobstructed vertical scrollbar.
