# One Dockerfile for every service:
#   docker build --build-arg SERVICE=Booking --build-arg PROJECT=Booking.Api -t bys/booking .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SERVICE
ARG PROJECT
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore "src/Services/${SERVICE}/${PROJECT}/${PROJECT}.csproj"
RUN dotnet publish "src/Services/${SERVICE}/${PROJECT}/${PROJECT}.csproj" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT
ENV ASPNETCORE_URLS=http://+:8080 \
    APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_DLL"]
