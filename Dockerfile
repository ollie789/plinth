# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props Plinth.sln ./
COPY src/Plinth.Core/Plinth.Core.csproj src/Plinth.Core/
COPY src/Plinth.Pipeline/Plinth.Pipeline.csproj src/Plinth.Pipeline/
COPY src/Plinth.Api/Plinth.Api.csproj src/Plinth.Api/
COPY src/Plinth.Cli/Plinth.Cli.csproj src/Plinth.Cli/
RUN dotnet restore src/Plinth.Cli/Plinth.Cli.csproj
COPY src/ src/
RUN dotnet publish src/Plinth.Cli/Plinth.Cli.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    PLINTH_STORE=none
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "plinth.dll"]
CMD ["version"]
