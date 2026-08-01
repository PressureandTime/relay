FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/Relay.Worker/Relay.Worker.csproj --locked-mode
RUN dotnet publish src/Relay.Worker/Relay.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/relay/keys \
    && chown -R "$APP_UID:$APP_UID" /var/relay
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Relay.Worker.dll"]
