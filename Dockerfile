FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/SlotGuard/SlotGuard.csproj src/SlotGuard/
RUN dotnet restore src/SlotGuard/SlotGuard.csproj
COPY src/SlotGuard/ src/SlotGuard/
RUN dotnet publish src/SlotGuard/SlotGuard.csproj -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /keys && chown "$APP_UID" /keys
COPY --from=build /publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SlotGuard.dll"]
