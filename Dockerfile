# --- сборка ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Chameleon.Server/Chameleon.Server.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

VOLUME /data
EXPOSE 8443
ENV CHAMELEON_LISTEN=0.0.0.0:8443 \
    CHAMELEON_KEY_FILE=/data/server.key \
    CHAMELEON_SNI=www.example-cdn.com
ENTRYPOINT ["dotnet", "Chameleon.Server.dll"]
