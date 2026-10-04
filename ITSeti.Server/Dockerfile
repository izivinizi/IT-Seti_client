FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ITSeti.Server/ITSeti.Server.csproj ITSeti.Server/
COPY ITSeti.Maintenance/src/ITSeti.Maintenance.Core/ITSeti.Maintenance.Core.csproj ITSeti.Maintenance/src/ITSeti.Maintenance.Core/
RUN dotnet restore ITSeti.Server/ITSeti.Server.csproj
COPY ITSeti.Server/ ITSeti.Server/
COPY ITSeti.Maintenance/src/ITSeti.Maintenance.Core/ ITSeti.Maintenance/src/ITSeti.Maintenance.Core/
COPY ITSeti.Maintenance/src/ITSeti.Maintenance.Infrastructure/Backend/allowed-processes.json ITSeti.Maintenance/src/ITSeti.Maintenance.Infrastructure/Backend/allowed-processes.json
COPY ITSeti.Maintenance/src/ITSeti.Maintenance.Infrastructure/Backend/allowed-publishers.txt ITSeti.Maintenance/src/ITSeti.Maintenance.Infrastructure/Backend/allowed-publishers.txt
RUN dotnet publish ITSeti.Server/ITSeti.Server.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataRoot=/data
RUN mkdir -p /data/packages /data/keys && chown -R $APP_UID:$APP_UID /data
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ITSeti.Server.dll"]
