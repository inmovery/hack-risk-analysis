{{- define "aeza-risk.fullname" -}}
{{- .Release.Name | trunc 50 | trimSuffix "-" -}}
{{- end -}}

{{- define "aeza-risk.labels" -}}
app.kubernetes.io/name: aeza-risk
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Values.image.tag | default .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: aeza-risk
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "aeza-risk.selectorLabels" -}}
app.kubernetes.io/name: aeza-risk
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "aeza-risk.appImage" -}}
{{ .Values.image.repository }}:{{ required "image.tag обязателен (тег образа сервиса)" .Values.image.tag }}
{{- end -}}

{{- define "aeza-risk.frontImage" -}}
{{ .Values.front.image.repository }}:{{ required "front.image.tag обязателен (тег образа панели)" .Values.front.image.tag }}
{{- end -}}

{{- define "aeza-risk.single" -}}
{{- if eq .Values.ingress.mode "single" }}true{{ end -}}
{{- end -}}

{{/* Адрес API и панели: в режиме single — один адрес продукта */}}
{{- define "aeza-risk.apiHost" -}}
{{- if include "aeza-risk.single" . -}}
{{ required "ingress.host обязателен (адрес продукта)" .Values.ingress.host }}
{{- else -}}
{{ required "ingress.api.host обязателен (домен API)" .Values.ingress.api.host }}
{{- end -}}
{{- end -}}

{{- define "aeza-risk.frontHost" -}}
{{- if include "aeza-risk.single" . -}}
{{ required "ingress.host обязателен (адрес продукта)" .Values.ingress.host }}
{{- else -}}
{{ required "ingress.front.host обязателен (домен панели)" .Values.ingress.front.host }}
{{- end -}}
{{- end -}}

{{/* Адрес API для браузера; порт ingress.publicPort — только для локального стенда (k3s в Docker с пробросом порта) */}}
{{- define "aeza-risk.publicSuffix" -}}
{{- with .Values.ingress.publicPort }}:{{ . }}{{- end -}}
{{- end -}}

{{/* Реплики на разных узлах, если это возможно; перенос с недоступного узла быстрее стандартных 300 с */}}
{{- define "aeza-risk.spread" -}}
affinity:
  podAntiAffinity:
    preferredDuringSchedulingIgnoredDuringExecution:
      - weight: 100
        podAffinityTerm:
          topologyKey: kubernetes.io/hostname
          labelSelector:
            matchLabels:
              {{- include "aeza-risk.selectorLabels" .root | nindent 14 }}
              app.kubernetes.io/component: {{ .component }}
tolerations:
  - key: node.kubernetes.io/unreachable
    operator: Exists
    effect: NoExecute
    tolerationSeconds: {{ .values.unreachableTolerationSeconds }}
  - key: node.kubernetes.io/not-ready
    operator: Exists
    effect: NoExecute
    tolerationSeconds: {{ .values.unreachableTolerationSeconds }}
  {{- with .values.tolerations }}
  {{- toYaml . | nindent 2 }}
  {{- end }}
{{- end -}}

{{- define "aeza-risk.podSecurity" -}}
runAsNonRoot: true
seccompProfile:
  type: RuntimeDefault
{{- end -}}

{{- define "aeza-risk.containerSecurity" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end -}}

{{/* Ожидание базы перед hook-ами: Job-ы не стартуют раньше, чем PostgreSQL принимает соединения */}}
{{- define "aeza-risk.waitForDb" -}}
- name: wait-for-db
  image: "{{ .Values.hooks.image.repository }}:{{ .Values.hooks.image.tag }}"
  imagePullPolicy: {{ .Values.hooks.image.pullPolicy }}
  command: ["/bin/sh", "-c"]
  args:
    - |
      for i in $(seq 1 120); do
        pg_isready -h {{ .Values.database.host }} -p {{ .Values.database.port }} -q && exit 0
        sleep 5
      done
      echo "PostgreSQL {{ .Values.database.host }}:{{ .Values.database.port }} недоступен 10 минут" >&2
      exit 1
  securityContext:
    runAsUser: 70
    runAsGroup: 70
    {{- include "aeza-risk.containerSecurity" . | nindent 4 }}
  resources:
    requests:
      cpu: 10m
      memory: 16Mi
    limits:
      memory: 64Mi
{{- end -}}

{{/* Путь /metrics в Ingress (точное совпадение) — при ingress.publicMetrics */}}
{{- define "aeza-risk.metricsPath" -}}
{{- if .Values.ingress.publicMetrics }}
- path: /metrics
  pathType: Exact
  backend:
    service:
      name: {{ include "aeza-risk.fullname" . }}-app
      port:
        name: http
{{- end }}
{{- end }}
