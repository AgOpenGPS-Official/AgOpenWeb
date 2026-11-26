# nuget-local

A NuGet package source (see `nuget.config`) for packages that are not on nuget.org. The
build fills it: `Shared/AgOpenWeb.VirtualTerminal` downloads the SharpIsoTerm package it
pins from SharpIsoTerm's GitHub release and checks its SHA-256. The packages are not
committed (`*.nupkg` is ignored); delete them to fetch again.
