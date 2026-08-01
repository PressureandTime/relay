FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /source
COPY . .
RUN dotnet restore tools/Relay.ReceiverSimulator/Relay.ReceiverSimulator.csproj --locked-mode
RUN dotnet publish tools/Relay.ReceiverSimulator/Relay.ReceiverSimulator.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Relay.ReceiverSimulator.dll"]
