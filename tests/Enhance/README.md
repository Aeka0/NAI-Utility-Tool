# Enhance regression checks

Build the app for x64, then run `dotnet run --project tests/Enhance -c Debug`.
For an isolated app build, set the MSBuild property `NaitoolBuildDirectory` to its
`bin` directory and pass the same directory as the first program argument.

The checks cover settings normalization, scale availability, dimensions, image
preparation, transparency, request parameters and the Max output cost limit.
HTTP is intercepted in memory with a synthetic token. The checks never load or
save user settings and do not contact NovelAI.

Manually check the Enhance dialog in both themes: the confirmation button should
show the Anlas icon and amount in gold when paid, and the regular accent when
free. The summary should only show output size, strength and noise. Also check
cleared number fields, cancelling, remembered settings, and the result/history
entry points.
