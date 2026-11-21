variable "region" {
  type    = string
  default = "us-east-1"
}

variable "domain" {
  description = "Subdomain that points at the Elastic IP (A record at Porkbun)."
  type        = string
  default     = "jam.pmullins.dev"
}

variable "admin_cidr" {
  description = "CIDR allowed to SSH in, e.g. \"203.0.113.7/32\"."
  type        = string
}

variable "alert_email" {
  description = "Where AWS Budgets sends spend alerts."
  type        = string
}

variable "ssh_public_key_path" {
  type    = string
  default = "~/.ssh/nov26jam.pub"
}

variable "instance_type" {
  type    = string
  default = "t3.micro"
}

variable "budget_usd" {
  type    = string
  default = "25"
}
