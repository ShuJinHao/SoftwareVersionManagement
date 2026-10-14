FROM mcr.microsoft.com/dotnet/aspnet:8.0.31-bookworm-slim@sha256:2f202e1169ec507bdc07007cf68c14d0ff3a098110b17c460a60185e1f36a9d1
COPY ca.crt /usr/local/share/ca-certificates/svm-deployment.crt
RUN update-ca-certificates
COPY release/ /opt/svm/
WORKDIR /opt/svm
USER 10001:10001
ENTRYPOINT ["dotnet"]
CMD ["/opt/svm/Svm.HttpApi/Svm.HttpApi.dll"]
