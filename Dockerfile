FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/Agent3d/Agent3d.csproj --locked-mode
RUN dotnet publish src/Agent3d/Agent3d.csproj --no-restore -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8088
ENTRYPOINT ["dotnet", "Agent3d.dll"]
