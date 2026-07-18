# Pole Functions

Official protocol contracts and .NET Agent SDK for running user Functions on Pole.

This repository is MIT licensed. Applications built with the SDK keep their own license and do not
inherit the license of the Pole control plane.

## Packages

- `Pole.Functions.Protocol` contains the versioned HTTP/JSON Agent protocol contracts and constants.
  It has no dependency on ASP.NET Core or Pole control-plane models.
- `Pole.Functions.Sdk` contains the .NET handler API and the in-process Agent runtime.

Both packages are released together. Their NuGet package version is independent from the wire
protocol version exposed by `PoleFunctionProtocol.Version`.

## User application

```csharp
builder.Services
    .AddPoleFunctions(
        builder.Configuration.GetSection(PoleFunctionsOptions.SectionName))
    .AddHttpHandler("hello", (request, context, cancellationToken) =>
        ValueTask.FromResult(PoleHttpResponse.Text("Hello")));

var app = builder.Build();
app.MapPoleFunctionsAgent();
app.Run();
```

One application image can register multiple HTTP and Timer handlers. The SDK supports delegate
handlers and scoped DI handlers through `IPoleHttpFunctionHandler` and
`IPoleTimerFunctionHandler`.

## Agent lifecycle

The Pole runtime sends reserve, execute, and status requests to the same Agent instance:

1. `POST /_pole/v1/attempts:reserve` acquires app and handler concurrency without running user code.
2. `POST /_pole/v1/attempts/{attemptId}:execute` starts user code once and returns `202 Accepted`.
3. `GET /_pole/v1/attempts/{attemptId}?waitUpToMs=...` observes or long-polls the result.

Repeated reserve and execute calls for the same immutable attempt are idempotent. Cancelling an
observation request does not cancel user code. Reservation leases and terminal attempt retention
are bounded.

Agent protocol endpoints deny requests by default. Pole injects the trusted signing keys and
identity settings required by the SDK's JWT authenticator. Health endpoints remain available for
container probes.

## Repository layout

```text
src/
  Pole.Functions.Protocol/
  Pole.Functions.Sdk/
samples/
  Pole.Functions.Sample/
tests/
  Pole.Functions.Tests/
```

## Build

```powershell
dotnet restore Pole.Functions.slnx
dotnet build Pole.Functions.slnx --no-restore
dotnet test Pole.Functions.slnx --no-build
dotnet pack src/Pole.Functions.Protocol --no-build -o artifacts
dotnet pack src/Pole.Functions.Sdk --no-build -o artifacts
```

The sample container can be built from the repository root:

```powershell
docker build -f samples/Pole.Functions.Sample/Dockerfile .
```

## Releases

CI builds, tests, and packs every pull request. Publishing is triggered manually with an explicit
package version or by pushing a `v*` tag. The publishing workflow uses NuGet.org Trusted Publishing
and the protected `nuget` GitHub environment; no long-lived NuGet API key is stored in GitHub.
