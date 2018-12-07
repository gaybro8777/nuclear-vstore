#!/bin/bash
set -e

dotnet restore --runtime alpine.3.8-x64
dotnet test tests/VStore.UnitTests/VStore.UnitTests.csproj --configuration Release
rm -rf $(pwd)/publish/vstore
dotnet publish src/VStore.Host --configuration Release --runtime alpine.3.8-x64 --output $(pwd)/publish/vstore
