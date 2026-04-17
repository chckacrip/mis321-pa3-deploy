FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish backend/mis-321-pa3-chckacrip.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/out .
COPY frontend ./frontend
ENV FRONTEND_PATH=/app/frontend
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["./mis-321-pa3-chckacrip"]
