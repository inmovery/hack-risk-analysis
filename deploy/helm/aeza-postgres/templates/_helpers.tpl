{{- define "aeza-postgres.fullname" -}}
{{- .Release.Name | trunc 50 | trimSuffix "-" -}}
{{- end -}}

{{/* Сервис с текущим primary: в режиме cnpg его создаёт оператор, в режиме single — чарт */}}
{{- define "aeza-postgres.rwHost" -}}
{{ include "aeza-postgres.fullname" . }}-rw
{{- end -}}

{{- define "aeza-postgres.labels" -}}
app.kubernetes.io/name: aeza-postgres
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: aeza-risk
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{/* Метки пода базы в режиме single */}}
{{- define "aeza-postgres.selectorLabels" -}}
app.kubernetes.io/name: aeza-postgres
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: database
{{- end -}}

{{/* Общий фрагмент securityContext контейнера: без root, без привилегий, только чтение корневой ФС */}}
{{- define "aeza-postgres.containerSecurity" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end -}}
