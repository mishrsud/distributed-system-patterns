FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy build configuration and project files first so restore is cached.
COPY global.json Directory.Build.props Directory.Packages.props TransactionalOutbox.slnx .editorconfig ./
COPY src/TransactionalOutbox.Core/TransactionalOutbox.Core.csproj src/TransactionalOutbox.Core/
COPY src/TransactionalOutbox.UseCases/TransactionalOutbox.UseCases.csproj src/TransactionalOutbox.UseCases/
COPY src/TransactionalOutbox.Infrastructure/TransactionalOutbox.Infrastructure.csproj src/TransactionalOutbox.Infrastructure/
COPY src/TransactionalOutbox.Web/TransactionalOutbox.Web.csproj src/TransactionalOutbox.Web/
RUN dotnet restore src/TransactionalOutbox.Web/TransactionalOutbox.Web.csproj

COPY src/ src/
RUN dotnet publish src/TransactionalOutbox.Web/TransactionalOutbox.Web.csproj \
    --no-restore -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "TransactionalOutbox.Web.dll"]
