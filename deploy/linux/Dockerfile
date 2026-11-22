FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586
COPY ca.crt /usr/local/share/ca-certificates/svm-deployment.crt
RUN update-ca-certificates
COPY release/ /opt/svm/
# Restored backup directories are private on the host. Published application
# files contain no deployment secrets and must be readable by the chosen UID.
RUN chmod -R a+rX /opt/svm
WORKDIR /opt/svm
USER 10001:10001
ENTRYPOINT ["dotnet"]
CMD ["/opt/svm/Svm.HttpApi/Svm.HttpApi.dll"]
