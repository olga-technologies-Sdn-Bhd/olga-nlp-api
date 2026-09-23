FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["NuGet.Config", "Directory.Build.props", "./"]
COPY ["src/Olga.Nlp.Worker/Olga.Nlp.Worker.csproj", "src/Olga.Nlp.Worker/"]
COPY ["src/Olga.Nlp.Application/Olga.Nlp.Application.csproj", "src/Olga.Nlp.Application/"]
COPY ["src/Olga.Nlp.Contracts/Olga.Nlp.Contracts.csproj", "src/Olga.Nlp.Contracts/"]
COPY ["src/Olga.Nlp.Domain/Olga.Nlp.Domain.csproj", "src/Olga.Nlp.Domain/"]
COPY ["src/Olga.Nlp.Infrastructure/Olga.Nlp.Infrastructure.csproj", "src/Olga.Nlp.Infrastructure/"]
RUN dotnet restore "src/Olga.Nlp.Worker/Olga.Nlp.Worker.csproj" --configfile NuGet.Config

COPY . .
RUN dotnet publish "src/Olga.Nlp.Worker/Olga.Nlp.Worker.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Olga.Nlp.Worker.dll"]
