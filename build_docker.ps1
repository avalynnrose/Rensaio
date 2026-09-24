$backendPath = "./RensaioBackend"
$project = "RensaioBackend.csproj"

Push-Location $backendPath
dotnet restore $project
dotnet publish $project -c Release -r linux-x64 --self-contained false -p:PublishAot=false -p:PublishReadyToRun=false -p:DebugSymbols=false -o bin/linux/amd64
dotnet publish $project -c Release -r linux-arm64 --self-contained false -p:PublishAot=false -p:PublishReadyToRun=false -p:DebugSymbols=false -o bin/linux/arm64

# Publish the isolated Mihon/IKVM/CEF process beside the backend (in an "rpc"
# subfolder to avoid an appsettings.json collision). The RPC child inherits the
# container's IKVM LD_LIBRARY_PATH and Xvfb display set by entrypoint.sh.
dotnet publish "..\Rensaio.RPC\Rensaio.RPC.csproj" -c Release -r linux-x64 --self-contained false -p:PublishAot=false -p:PublishReadyToRun=false -p:DebugSymbols=false -o bin/linux/amd64/rpc
dotnet publish "..\Rensaio.RPC\Rensaio.RPC.csproj" -c Release -r linux-arm64 --self-contained false -p:PublishAot=false -p:PublishReadyToRun=false -p:DebugSymbols=false -o bin/linux/arm64/rpc

docker buildx build -t maxpiva/rensaio:latest --platform linux/amd64,linux/arm64 . --push
Pop-Location

