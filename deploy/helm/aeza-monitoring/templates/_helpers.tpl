{{- define "aeza-monitoring.fullname" -}}
{{- .Release.Name | trunc 50 | trimSuffix "-" -}}
{{- end -}}

{{- define "aeza-monitoring.labels" -}}
app.kubernetes.io/name: aeza-monitoring
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: aeza-risk
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "aeza-monitoring.selectorLabels" -}}
app.kubernetes.io/name: aeza-monitoring
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "aeza-monitoring.containerSecurity" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end -}}
