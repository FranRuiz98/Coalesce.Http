FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Build.props
COPY README.md README.md
COPY Stampede.Http/ Stampede.Http/
COPY samples/Stampede.Http.Sample.Client/ samples/Stampede.Http.Sample.Client/
RUN dotnet publish samples/Stampede.Http.Sample.Client -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Stampede.Http.Sample.Client.dll"]
