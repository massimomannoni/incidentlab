FROM node:22-bookworm-slim AS frontend
WORKDIR /ui
COPY frontend/package*.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend
WORKDIR /src
COPY Directory.Build.props ./
COPY backend/IncidentLab.Core/ backend/IncidentLab.Core/
COPY backend/IncidentLab.Api/ backend/IncidentLab.Api/
RUN dotnet restore backend/IncidentLab.Api/IncidentLab.Api.csproj --locked-mode \
    && dotnet publish backend/IncidentLab.Api/IncidentLab.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=backend /out ./
COPY --from=frontend /ui/dist ./frontend
ENV FRONTEND_DIR=/app/frontend ASPNETCORE_URLS=http://+:8010
USER $APP_UID
EXPOSE 8010
# One process owns scenario scheduling and incident transitions in this POC.
ENTRYPOINT ["dotnet", "IncidentLab.Api.dll"]
