# Chat.IntegrationTests

This is a dependency-free executable integration suite. It starts a real
`Chat.Server` process for each scenario, connects real TCP peers, and exits
with code `0` only when every scenario passes.

From the `outputs/Lab1Chat` directory, build the shared library and server,
then run:

```powershell
dotnet build src/Chat.Server/Chat.Server.csproj
dotnet run --project tests/Chat.IntegrationTests/Chat.IntegrationTests.csproj -- --server src/Chat.Server/bin/Debug/net9.0/Chat.Server.dll
```

The server path can also be supplied via `CHAT_SERVER_DLL`. The harness passes
`--address 127.0.0.1 --port <free-port>` to every server process it owns.
