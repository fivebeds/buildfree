# Builds BuildFree (the API and the web app) into one container image that
# any host that runs Docker can use. It listens on port 8080.
#   docker build -t buildfree .
#   docker run -p 8080:8080 -e DATABASE_URL=... -e Jwt__Key=... buildfree
# See "Going online" in the README for every setting.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BuildFree.Api/BuildFree.Api.csproj BuildFree.Api/
RUN dotnet restore BuildFree.Api/BuildFree.Api.csproj
COPY BuildFree.Api/ BuildFree.Api/
RUN dotnet publish BuildFree.Api/BuildFree.Api.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY web/ wwwroot/
# Photos go here unless Photos:Storage is S3. Mount a volume on /data to keep
# them, or use S3 storage on hosts whose disks are wiped on every deploy.
RUN mkdir -p /data/photos && chown "$APP_UID" /data/photos
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Photos__Path=/data/photos
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "BuildFree.Api.dll"]
