FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Build.props
COPY samples/Stampede.Http.Sample.Api/ samples/Stampede.Http.Sample.Api/
RUN dotnet publish samples/Stampede.Http.Sample.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is here only so docker compose can run a real HTTP healthcheck against /health.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Stampede.Http.Sample.Api.dll"]
