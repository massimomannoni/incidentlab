FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY backend/IncidentLab.Gateway/ backend/IncidentLab.Gateway/
RUN dotnet restore backend/IncidentLab.Gateway/IncidentLab.Gateway.csproj --locked-mode \
    && dotnet publish backend/IncidentLab.Gateway/IncidentLab.Gateway.csproj -c Release --no-restore -o /out
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8020
USER $APP_UID
EXPOSE 8020
ENTRYPOINT ["dotnet", "IncidentLab.Gateway.dll"]
