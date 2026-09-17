#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
dotnet restore Shortener.slnx --configfile NuGet.Config --disable-parallel -m:1
dotnet build Shortener.slnx -c Release --no-restore -m:1
export DOTNET_HOST_PATH="$(command -v dotnet)"
dotnet tests/Shortener.Tests/bin/Release/net10.0/Shortener.Tests.dll --integration
