FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ITSeti.Server.csproj ./
RUN dotnet restore ITSeti.Server.csproj
COPY . ./
RUN dotnet publish ITSeti.Server.csproj -c Release -o /out --no-restore /p:UseAppHost=false

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
